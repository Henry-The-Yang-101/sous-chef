#!/usr/bin/env python3
"""Build and run a tiny native Apple SpeechTranscriber terminal tester."""
import os
from pathlib import Path
import platform
import plistlib
import shutil
import subprocess
import sys


def main():
    if sys.platform != 'darwin' or int(platform.mac_ver()[0].split('.')[0]) < 27:
        sys.exit('This tester requires macOS 27 or later (native microphone capture API).')
    if not shutil.which('xcrun'):
        sys.exit('Install Xcode or its command-line tools to build the Swift tester.')

    root = Path(__file__).resolve().parents[1]
    source = root / 'scripts/apple_speech_test.swift'
    build = root / '.local/apple-speech-test'
    binary = build / 'apple-speech-test'
    if not binary.exists() or binary.stat().st_mtime < max(source.stat().st_mtime, Path(__file__).stat().st_mtime):
        build.mkdir(parents=True, exist_ok=True)
        info = build / 'Info.plist'
        info.write_bytes(plistlib.dumps({
            'CFBundleIdentifier': 'dev.souschef.apple-speech-test',
            'CFBundleName': 'Apple Speech Test',
            'NSMicrophoneUsageDescription': 'Transcribe your microphone locally to test Apple SpeechTranscriber.',
            'NSSpeechRecognitionUsageDescription': 'Test on-device Apple speech transcription.',
        }))
        print('Building Apple SpeechTranscriber tester...', flush=True)
        temporary = build / 'apple-speech-test.building'
        try:
            subprocess.run([
                'xcrun', 'swiftc', '-parse-as-library', '-O', '-swift-version', '6',
                '-target', f'{platform.machine()}-apple-macosx27.0', str(source),
                '-Xlinker', '-sectcreate', '-Xlinker', '__TEXT', '-Xlinker', '__info_plist',
                '-Xlinker', str(info), '-o', str(temporary),
            ], check=True)
        except subprocess.CalledProcessError as error:
            return error.returncode
        temporary.replace(binary)
    os.execv(str(binary), [str(binary), *sys.argv[1:]])


if __name__ == '__main__':
    sys.exit(main())
