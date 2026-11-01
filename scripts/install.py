#!/usr/bin/env python3
"""Reversible side-by-side install; game bundle and Steam settings stay untouched."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
from build import DEFAULT_GAME, build
from toolchain import ROOT, TOOLS, bootstrap


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def install(game):
    output = build(game)
    dest = game.parent
    manifest = dest / '.sous-chef-install.json'
    previous = json.loads(manifest.read_text()) if manifest.exists() else {'files': {}}
    files = {}
    for source in (TOOLS / 'bepinex').rglob('*'):
        if source.is_file() and source.name not in ('changelog.txt',):
            files[str(source.relative_to(TOOLS / 'bepinex'))] = source
    files['BepInEx/plugins/SousChef/SousChef.Bridge.dll'] = output / 'SousChef.Bridge.dll'
    files['BepInEx/plugins/SousChef/build.json'] = output / 'build.json'
    # Check every destination before writing any of them.
    for relative, source in files.items():
        target = dest / relative
        if target.exists() and sha(target) != sha(source) and sha(target) != previous['files'].get(relative):
            raise SystemExit('Refusing to overwrite an unowned/modified file: ' + str(target))
    owned = dict(previous['files'])
    for relative, source in files.items():
        target = dest / relative
        # Do not take ownership of a pre-existing identical loader belonging to another mod.
        owns = not target.exists() or relative in owned
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
        if owns: owned[relative] = sha(target)
    (dest / 'run_bepinex.sh').chmod(0o755)
    manifest.write_text(json.dumps({'version': 1, 'files': owned}, indent=2) + '\n')
    print('Installed observer. Launch with: python3 scripts/launch.py')
    print('Steam launch option (optional): "' + str(dest / 'run_bepinex.sh') + '" %command%')


def uninstall(game):
    dest = game.parent
    manifest = dest / '.sous-chef-install.json'
    if not manifest.exists(): raise SystemExit('No owned installation manifest found.')
    data = json.loads(manifest.read_text()); remaining = {}
    for relative, digest in data['files'].items():
        target = dest / relative
        if not target.resolve().is_relative_to(dest.resolve()):
            raise SystemExit('Invalid manifest path')
        if target.exists():
            if sha(target) != digest:
                print('Preserving modified file:', target); remaining[relative] = digest; continue
            target.unlink()
    if remaining:
        data['files'] = remaining; manifest.write_text(json.dumps(data, indent=2) + '\n')
    else: manifest.unlink()
    print('Removed owned binaries; generated configs/logs and other mods were preserved.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game', type=Path, default=Path(os.environ.get('OC2_GAME', DEFAULT_GAME)))
    parser.add_argument('--uninstall', action='store_true')
    args = parser.parse_args()
    (uninstall if args.uninstall else install)(args.game.resolve())
