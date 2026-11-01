#!/usr/bin/env python3
"""Fetch pinned development tools locally; never modify system toolchains."""
import hashlib
import json
from pathlib import Path
import platform
import tarfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
TOOLS = ROOT / '.tools'
SDK = '8.0.408'
BEPINEX = '5.4.23.6'
BEPINEX_SHA256 = '2e711e21f6570da964fce4654b1976500dbe043f61778bce0008500078564638'


def download(url, path, digest, algorithm):
    if not path.exists() or hashlib.new(algorithm, path.read_bytes()).hexdigest() != digest:
        print('Downloading', path.name, flush=True)
        partial = path.with_suffix('.part')
        urllib.request.urlretrieve(url, partial)
        if hashlib.new(algorithm, partial.read_bytes()).hexdigest() != digest:
            partial.unlink()
            raise RuntimeError('Checksum mismatch: ' + url)
        partial.replace(path)


def bootstrap():
    TOOLS.mkdir(exist_ok=True)
    archive = TOOLS / 'bepinex.zip'
    download(f'https://github.com/BepInEx/BepInEx/releases/download/v{BEPINEX}/'
             f'BepInEx_macos_universal_{BEPINEX}.zip', archive, BEPINEX_SHA256, 'sha256')
    if not (TOOLS / 'bepinex/BepInEx/core/BepInEx.dll').exists():
        with zipfile.ZipFile(archive) as z:
            z.extractall(TOOLS / 'bepinex')
    dotnet = TOOLS / 'dotnet/dotnet'
    if not dotnet.exists():
        os_name = {'Darwin': 'osx', 'Linux': 'linux'}[platform.system()]
        arch = 'arm64' if platform.machine() in ('arm64', 'aarch64') else 'x64'
        rid = f'{os_name}-{arch}'
        with urllib.request.urlopen('https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json') as r:
            releases = json.load(r)
        sdk = next(s for r in releases['releases'] for s in r.get('sdks', []) if s['version'] == SDK)
        entry = next(f for f in sdk['files'] if f['rid'] == rid and f['name'].endswith('.tar.gz'))
        archive = TOOLS / f'dotnet-{SDK}-{rid}.tar.gz'
        download(entry['url'], archive, entry['hash'], 'sha512')
        dest = TOOLS / 'dotnet'
        dest.mkdir(exist_ok=True)
        with tarfile.open(archive) as t:
            t.extractall(dest, filter='data')
    return dotnet


if __name__ == '__main__':
    print(bootstrap())
