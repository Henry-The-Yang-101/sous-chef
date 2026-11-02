#!/usr/bin/env python3
"""Compile against the installed game's exact Mono/Unity profile (no NuGet)."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
from toolchain import ROOT, TOOLS, SDK, bootstrap

DEFAULT_GAME = Path.home() / 'Library/Application Support/Steam/steamapps/common/Overcooked! 2/Overcooked2.app'


def build(game):
    dotnet = bootstrap()
    managed = game / 'Contents/Resources/Data/Managed'
    if not (managed / 'Assembly-CSharp.dll').is_file():
        raise SystemExit('Game assemblies not found: ' + str(managed))
    output = ROOT / 'artifacts/plugin'
    output.mkdir(parents=True, exist_ok=True)
    sources = sorted((ROOT / 'src').rglob('*.cs'))
    references = [managed / n for n in ('mscorlib.dll', 'System.dll', 'System.Core.dll',
                                      'Assembly-CSharp.dll', 'Assembly-CSharp-firstpass.dll')]
    references += sorted(managed.glob('UnityEngine*.dll'))
    references += [TOOLS / 'bepinex/BepInEx/core/BepInEx.dll']
    references += [TOOLS / 'bepinex/BepInEx/core/0Harmony.dll']
    args = ['-nostdlib+', '-target:library', '-langversion:7.3', '-optimize+',
            '-warnaserror+', '-out:' + str(output / 'SousChef.Bridge.dll')]
    args += ['-r:' + str(p) for p in references] + [str(p) for p in sources]
    response = output / 'compile.rsp'
    response.write_text('\n'.join('"' + a + '"' for a in args))
    subprocess.run([str(dotnet), str(TOOLS / f'dotnet/sdk/{SDK}/Roslyn/bincore/csc.dll'),
                    '-noconfig', '@' + str(response)], check=True, cwd=ROOT)
    fingerprints = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in references
                    if p.name in ('Assembly-CSharp.dll', 'Assembly-CSharp-firstpass.dll', 'BepInEx.dll')}
    (output / 'build.json').write_text(json.dumps({'sdk': SDK, 'references': fingerprints}, indent=2) + '\n')
    subprocess.run([str(dotnet), 'publish', str(ROOT / 'worker/Worker.csproj'), '-c', 'Release',
                    '-o', str(output / 'worker'), '--nologo'], check=True, cwd=ROOT)
    (output / 'worker-dotnet.txt').write_text(str(dotnet.resolve()) + '\n')
    print('Built', output / 'SousChef.Bridge.dll')
    return output


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game', type=Path, default=Path(os.environ.get('OC2_GAME', DEFAULT_GAME)))
    build(parser.parse_args().game.resolve())
