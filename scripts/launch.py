#!/usr/bin/env python3
"""Run the installed bridge through the pinned BepInEx wrapper."""
import argparse
import os
from pathlib import Path
import subprocess
from build import DEFAULT_GAME

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game', type=Path, default=Path(os.environ.get('OC2_GAME', DEFAULT_GAME)))
    args, rest = parser.parse_known_args()
    game = args.game.resolve()
    wrapper = game.parent / 'run_bepinex.sh'
    if not wrapper.exists(): raise SystemExit('Run python3 scripts/install.py first.')
    isolated = bool(os.environ.get('SOUS_CHEF_NATIVE_SIM')) and os.environ.get('SOUS_CHEF_NATIVE_PORT_BASE', '24702') != '24702' and '-batchmode' in rest and '-nographics' in rest
    if not isolated and subprocess.run(['pgrep', '-x', 'Overcooked2'], capture_output=True).returncode == 0:
        raise SystemExit('Overcooked! 2 is already running. Quit it normally before relaunching with the bridge.')
    env = dict(os.environ, SteamAppId='728880', SteamGameId='728880')
    os.chdir(game.parent)
    os.execve(wrapper, [str(wrapper), str(game)] + rest, env)
