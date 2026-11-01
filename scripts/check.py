#!/usr/bin/env python3
"""Build the real plugin, test transport/client invariants, audit installed APIs."""
import argparse
import os
from pathlib import Path
import subprocess
import sys
from build import DEFAULT_GAME, build
from toolchain import ROOT, bootstrap

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game', type=Path, default=Path(os.environ.get('OC2_GAME', DEFAULT_GAME)))
    args = parser.parse_args()
    build(args.game.resolve())
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1')
    subprocess.run([str(bootstrap()), 'run', '--project', 'tests/Bridge.Tests.csproj', '--',
                    str(args.game.resolve() / 'Contents/Resources/Data/Managed')], cwd=ROOT, env=env, check=True)
    subprocess.run([str(bootstrap()), 'run', '--project', 'tests/navigation/Navigation.Tests.csproj'], cwd=ROOT, env=env, check=True)
    subprocess.run([sys.executable, '-m', 'unittest', 'discover', '-s', 'tests', '-p', 'test_*.py', '-v'], cwd=ROOT, check=True)
