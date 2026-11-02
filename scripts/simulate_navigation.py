#!/usr/bin/env python3
"""Run the recorded-stair motion suite without launching or contacting the game."""
import argparse
import os
from pathlib import Path
import subprocess
from toolchain import ROOT, bootstrap


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--trace-dir', type=Path, help='Write per-frame NDJSON for every scenario')
    parser.add_argument('--verbose-trace', action='store_true', help='Also retain controller waypoints and rejected physics checks')
    args = parser.parse_args()
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1')
    env['SOUS_CHEF_NAV_TRACE'] = '1' if args.verbose_trace else '0'
    command = [str(bootstrap()), 'run', '--project', 'tests/simulation/Simulation.Tests.csproj', '--', '--suite']
    if args.trace_dir:
        command.append(str(args.trace_dir.resolve()))
    raise SystemExit(subprocess.run(command, cwd=ROOT, env=env).returncode)
