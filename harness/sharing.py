"""Phone URLs, offline QR generation and a managed Cloudflare tunnel."""
import ipaddress
import json
from http.client import HTTPException
import re
import secrets
import shutil
import socket
import subprocess
import sys
import threading
import time
from pathlib import Path
from urllib.parse import urlsplit
from urllib.request import Request, urlopen

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
    HEALTH_INTERVAL = 15
    FAILURE_LIMIT = 3
    STARTUP_TIMEOUT = 90
    RATE_LIMIT_RETRY_DELAY = 120
    MAX_RETRY_DELAY = 900

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
        self.monitor = None
        self.halt = threading.Event()
        self.health_changed = threading.Event()
        self.lifecycle_lock = threading.Lock()
        self.tunnel_url = None
        self.tunnel_started = 0
        self.tunnel_failures = 0
        self.tunnel_verified = False
        self.tunnel_exit = None
        self.retry_delay = self.HEALTH_INTERVAL
        self.command = None
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
            if (parsed.netloc in self.local_hosts
                    or any(urlsplit(link['url']).netloc == parsed.netloc for link in self.links)):
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
        self.command = [executable, 'tunnel', '--no-autoupdate', '--url', f'http://127.0.0.1:{port}']
        self._launch_tunnel(initial=True)
        self.monitor = threading.Thread(target=self._monitor_tunnel, name='phone-health', daemon=True)
        self.monitor.start()

    def _launch_tunnel(self, *, initial=False):
        self.process = subprocess.Popen(
            self.command,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, errors='replace')
        with self.lock:
            self.tunnel_url = None
            self.tunnel_started = time.monotonic()
            self.tunnel_failures = 0
            self.tunnel_verified = False
            self.tunnel_exit = None
            self.status = 'Starting the Cloudflare internet link…'
            self.internet_pending = initial
        self.reader = threading.Thread(target=self._read_tunnel, args=(self.process,), name='phone-tunnel', daemon=True)
        self.reader.start()

    def _hide_tunnel(self):
        if self.tunnel_url:
            host = urlsplit(self.tunnel_url).netloc
            self.origins.pop(host, None)
            self.links[:] = [link for link in self.links if urlsplit(link['url']).netloc != host]

    def _read_tunnel(self, process):
        public_url = None
        provisioning_status = None
        exit_code = None
        try:
            for line in process.stdout:
                # CLI errors can be plain text instead of timestamped ERR logs.
                level = re.match(r'^\S+ (DBG|INF|WRN|ERR|FTL) ', line)
                if line.strip() and (not level or level[1] in {'WRN', 'ERR', 'FTL'}):
                    print(f'[cloudflared] {line.rstrip()}', file=sys.stderr, flush=True)
                status = re.search(r'quick tunnel provisioning failed with status (\d{3})\b', line, re.I)
                if status:
                    provisioning_status = int(status[1])
                match = re.search(r'https://[a-z0-9-]+\.trycloudflare\.com\b', line)
                if match:
                    public_url = match[0]
                if public_url and 'Registered tunnel connection' in line:
                    with self.lock:
                        if process is self.process and not self.halt.is_set():
                            self.tunnel_url = public_url
                            self.status = 'Checking the Cloudflare internet link…'
                    self.health_changed.set()
            exit_code = process.wait()
        finally:
            process.stdout.close()
            with self.lock:
                if process is self.process:
                    self._hide_tunnel()
                    self.internet_pending = False
                    if not self.halt.is_set():
                        if provisioning_status == 429:
                            self.retry_delay = max(self.retry_delay, self.RATE_LIMIT_RETRY_DELAY)
                            reason = 'Cloudflare rate limited Quick Tunnel creation (HTTP 429)'
                        elif provisioning_status is not None:
                            reason = f'Cloudflare could not create a Quick Tunnel (HTTP {provisioning_status})'
                        else:
                            reason = f'Cloudflare internet link stopped (cloudflared exit code {exit_code})'
                        self.tunnel_exit = reason
                        self.status = reason
            self.health_changed.set()

    def _verify_tunnel(self, origin):
        # Check local DNS as well as HTTPS, even when Python uses an HTTP proxy.
        socket.getaddrinfo(urlsplit(origin).hostname, 443, type=socket.SOCK_STREAM)
        request = Request(f'{origin}/api/bootstrap', headers={'X-Page-Token': self.token})
        with urlopen(request, timeout=5) as response:
            data = json.loads(response.read(4096))
        if not isinstance(data, dict) or data.get('token') != self.token:
            raise ValueError('Tunnel did not reach this harness session')

    def _check_tunnel(self):
        """Hide failed links immediately; return whether a replacement is needed."""
        with self.lock:
            if self.halt.is_set():
                return False
            process, origin = self.process, self.tunnel_url
            if self.tunnel_exit is not None:
                return True
        # Let the reader drain the final diagnostic before deciding how to retry.
        if process.poll() is not None:
            return False
        try:
            if origin is None:
                if time.monotonic() - self.tunnel_started < self.STARTUP_TIMEOUT:
                    return False
                raise OSError('Cloudflare connection timed out')
            # Host validation must accept the candidate during the private probe,
            # before its QR link is published. Public API reads still need the token.
            with self.lock:
                if self.halt.is_set() or self.tunnel_exit is not None:
                    return self.tunnel_exit is not None
                self.origins[urlsplit(origin).netloc] = origin
            self._verify_tunnel(origin)
        except (OSError, ValueError, HTTPException) as error:
            with self.lock:
                if self.halt.is_set() or self.tunnel_exit is not None:
                    return self.tunnel_exit is not None
                self._hide_tunnel()
                self.tunnel_failures += 1
                self.status = ('Cloudflare internet link unavailable. Reconnecting; scan the new QR when ready.'
                               if self.tunnel_verified else 'Waiting for the Cloudflare internet link to become reachable…')
            print(f'[phone sharing] {origin or "Tunnel startup"}: {error}', file=sys.stderr, flush=True)
            # New hostnames can be negatively cached during DNS propagation.
            # Give that first probe the full startup allowance before rotating it.
            return (self.tunnel_failures >= self.FAILURE_LIMIT
                    and (self.tunnel_verified or time.monotonic() - self.tunnel_started >= self.STARTUP_TIMEOUT))
        with self.lock:
            if (not self.halt.is_set() and process is self.process
                    and process.poll() is None and origin == self.tunnel_url):
                self._add(origin, 'Internet')
                self.tunnel_failures = 0
                self.tunnel_verified = True
                self.retry_delay = self.HEALTH_INTERVAL
                self.internet_pending = False
                self.status = 'Cloudflare internet link ready. Keep your Mac awake and this harness running.'
        return False

    def _monitor_tunnel(self):
        while not self.halt.is_set():
            self.health_changed.wait(self.HEALTH_INTERVAL)
            self.health_changed.clear()
            if not self._check_tunnel():
                continue
            with self.lifecycle_lock:
                if self.halt.is_set():
                    break
                self._stop_tunnel()
            self._retry_tunnel(self.tunnel_exit)

    def _retry_tunnel(self, reason):
        while not self.halt.is_set():
            if reason:
                delay = self.retry_delay
                self.retry_delay = min(delay * 2, self.MAX_RETRY_DELAY)
                status = f'{reason}. Retrying in {delay} seconds.'
                with self.lock:
                    self.status = status
                    self.internet_pending = False
                print(f'[phone sharing] {status}', file=sys.stderr, flush=True)
                if self.halt.wait(delay):
                    return
            with self.lifecycle_lock:
                if self.halt.is_set():
                    return
                try:
                    self._launch_tunnel()
                except OSError as error:
                    reason = f'Could not restart cloudflared: {error}'
                else:
                    return

    def _stop_tunnel(self):
        with self.lock:
            process, self.process = self.process, None
            self._hide_tunnel()
            self.internet_pending = False
        if process is not None and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=2)
        if self.reader:
            self.reader.join(timeout=2)

    def close(self):
        self.halt.set()
        self.health_changed.set()
        with self.lifecycle_lock:
            self._stop_tunnel()
        if self.monitor:
            self.monitor.join(timeout=6)
