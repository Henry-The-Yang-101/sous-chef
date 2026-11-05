"""Phone URLs, offline QR generation and a managed Cloudflare tunnel."""
import ipaddress
import re
import secrets
import shutil
import socket
import subprocess
import sys
import threading
from pathlib import Path
from urllib.parse import urlsplit

from harness.vendor.qrcodegen import QrCode


def lan_addresses():
    addresses = []
    # UDP connect selects the default interface without sending a packet.
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
            probe.connect(('192.0.2.1', 9))
            addresses.append(probe.getsockname()[0])
    except OSError:
        pass
    if sys.platform == 'darwin':
        try:
            result = subprocess.run(['/sbin/ifconfig'], capture_output=True, text=True,
                                    check=True, timeout=2)
            addresses.extend(re.findall(r'\binet (\d+\.\d+\.\d+\.\d+)\b', result.stdout))
        except (OSError, subprocess.SubprocessError):
            pass
    try:
        addresses.extend(info[4][0] for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET))
    except OSError:
        pass
    return list(dict.fromkeys(address for address in addresses
                              if not ipaddress.ip_address(address).is_loopback
                              and not ipaddress.ip_address(address).is_unspecified))


def qr_svg(text):
    qr = QrCode.encode_text(text, QrCode.Ecc.MEDIUM)
    border = 4
    size = qr.get_size() + border * 2
    path = ' '.join(f'M{x + border},{y + border}h1v1h-1z'
                    for y in range(qr.get_size()) for x in range(qr.get_size())
                    if qr.get_module(x, y))
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {size} {size}" '
            f'width="{size * 6}" height="{size * 6}" shape-rendering="crispEdges">'
            f'<rect width="100%" height="100%" fill="#fff"/><path d="{path}" fill="#000"/></svg>')


class PhoneShare:
    def __init__(self, port, token, addresses=(), public_url=None):
        self.token = token
        self.lock = threading.RLock()
        self.origins = {f'127.0.0.1:{port}': f'http://127.0.0.1:{port}',
                        f'localhost:{port}': f'http://localhost:{port}'}
        self.local_hosts = set(self.origins)
        self.links = []
        self.status = None
        self.internet_pending = False
        self.process = None
        self.reader = None
        for address in addresses:
            self._add(f'http://{address}:{port}', 'Same Wi-Fi')
        if public_url:
            self._add(public_url, 'Internet')

    def _add(self, origin, label):
        parsed = urlsplit(origin)
        if (parsed.scheme not in {'http', 'https'} or not parsed.hostname
                or parsed.username is not None or parsed.password is not None
                or parsed.path not in {'', '/'} or parsed.query or parsed.fragment):
            raise ValueError('Sharing URL must be an http(s) origin without a path or credentials')
        origin = f'{parsed.scheme}://{parsed.netloc}'
        url = f'{origin}/#access={self.token}'
        with self.lock:
            if parsed.netloc in self.origins:
                return
            self.origins[parsed.netloc] = origin
            self.links.insert(0 if label == 'Internet' else len(self.links),
                              {'label': label, 'url': url, 'qrSvg': qr_svg(url)})

    def valid_origin(self, host, origin):
        with self.lock:
            expected = self.origins.get(host)
            return expected is not None and (origin is None or origin == expected)

    def is_local(self, host, peer):
        return host in self.local_hosts and ipaddress.ip_address(peer).is_loopback

    def authorized(self, token):
        return isinstance(token, str) and token.isascii() and secrets.compare_digest(token, self.token)

    def describe(self):
        with self.lock:
            return {'links': list(self.links), 'status': self.status,
                    'internetPending': self.internet_pending}

    def start_tunnel(self, port):
        executable = shutil.which('cloudflared')
        bundled = Path(__file__).resolve().parents[1] / '.tools/cloudflared'
        if executable is None and bundled.is_file():
            executable = str(bundled)
        if executable is None:
            raise RuntimeError('Install cloudflared first (brew install cloudflared)')
        self.process = subprocess.Popen(
            [executable, 'tunnel', '--no-autoupdate', '--url', f'http://127.0.0.1:{port}'],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        with self.lock:
            self.status = 'Starting the Cloudflare internet link…'
            self.internet_pending = True
        self.reader = threading.Thread(target=self._read_tunnel, name='phone-tunnel', daemon=True)
        self.reader.start()

    def _read_tunnel(self):
        process = self.process
        public_url = None
        try:
            for line in process.stdout:
                match = re.search(r'https://[a-z0-9-]+\.trycloudflare\.com\b', line)
                if match:
                    public_url = match[0]
                if public_url and 'Registered tunnel connection' in line:
                    with self.lock:
                        self._add(public_url, 'Internet')
                        self.internet_pending = False
                        self.status = 'Cloudflare internet link ready. Keep this harness running.'
            process.wait()
            with self.lock:
                for link in self.links[:]:
                    if link['label'] == 'Internet':
                        self.origins.pop(urlsplit(link['url']).netloc, None)
                        self.links.remove(link)
                self.internet_pending = False
                self.status = 'Internet tunnel stopped. Restart the harness to reconnect.'
        finally:
            process.stdout.close()

    def close(self):
        if self.process is not None:
            self.process.terminate()
            try:
                self.process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=2)
            if self.reader:
                self.reader.join(timeout=2)
