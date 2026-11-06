#!/usr/bin/env python3
"""Build/exec the local NDJSON speech helper; stdout is reserved for events."""
import hashlib
import os
from pathlib import Path
import platform
import plistlib
import shutil
import subprocess
import sys


def build():
    if sys.platform != 'darwin' or int(platform.mac_ver()[0].split('.')[0]) < 27:
        raise RuntimeError('Voice capture requires macOS 27 or later')
    if not shutil.which('xcrun'):
        raise RuntimeError('Install Xcode command-line tools to build the speech helper')
    source = Path(__file__).with_suffix('.swift')
    digest = hashlib.sha256(source.read_bytes() + Path(__file__).read_bytes()).hexdigest()[:16]
    folder = source.parents[1] / '.local' / 'apple-speech-service' / digest
    binary = folder / 'speech-service'
    if binary.exists():
        return binary
    folder.mkdir(parents=True, exist_ok=True)
    info = folder / 'Info.plist'
    info.write_bytes(plistlib.dumps({
        'CFBundleIdentifier': 'dev.souschef.speech-service',
        'CFBundleName': 'Sous Chef Voice',
        'NSMicrophoneUsageDescription': 'Transcribe Mac microphone commands locally for Sous Chef.',
        'NSSpeechRecognitionUsageDescription': 'Recognize Sous Chef commands on this Mac.',
    }))
    temporary = folder / f'speech-service.{os.getpid()}.building'
    try:
        subprocess.run(['xcrun', 'swiftc', '-parse-as-library', '-O', '-swift-version', '6',
                        '-target', f'{platform.machine()}-apple-macosx27.0', str(source),
                        '-Xlinker', '-sectcreate', '-Xlinker', '__TEXT', '-Xlinker', '__info_plist',
                        '-Xlinker', str(info), '-o', str(temporary)], check=True, stdout=sys.stderr)
        temporary.replace(binary)
    finally:
        temporary.unlink(missing_ok=True)
    return binary


if __name__ == '__main__':
    try:
        executable = build()
        if '--build-only' not in sys.argv:
            os.execv(str(executable), [str(executable)])
    except (RuntimeError, OSError, subprocess.CalledProcessError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
