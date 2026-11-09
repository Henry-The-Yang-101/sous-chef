"""Persistent async HTTPS, without an SDK, automatic redirects, or hidden retries.

Only the nonblocking write of request bytes runs under the voice dispatch gate.
DNS, TLS, drain, reads, backoff and all model waits run outside every game lock.
"""
import asyncio
from email.utils import parsedate_to_datetime
import json
import ssl
import time

from .protocol import DecisionError, validate_request, validate_response


class ProviderError(DecisionError):
    def __init__(self, status, retry_after=0):
        self.status, self.retry_after = status, retry_after
        message = {401: 'Jev authentication failed; check JEV_API_KEY',
                   403: 'Jev access was denied', 413: 'Jev context limit exceeded; shorten the command',
                   422: 'Jev rejected the request schema'}.get(status, f'Jev request failed (HTTP {status})')
        super().__init__(message)  # Never echo a remote body or authorization header.


def retry_seconds(value):
    try:
        return max(0, float(value))
    except (TypeError, ValueError):
        try:
            return max(0, parsedate_to_datetime(value).timestamp() - time.time())
        except (TypeError, ValueError, OverflowError):
            return 0


class HttpTransport:
    HOST = 'api.typesafe.ai'
    MAX_BODY = 1024 * 1024

    def __init__(self, api_key):
        self.api_key = api_key
        self.reader = self.writer = None

    async def close(self):
        writer, self.writer, self.reader = self.writer, None, None
        if writer:
            writer.close()
            try:
                await asyncio.wait_for(writer.wait_closed(), 1)
            except (OSError, TimeoutError):
                pass

    async def exchange(self, request, send):
        if not self.api_key:
            raise DecisionError('Add JEV_API_KEY to .env and restart the harness')
        body = json.dumps(validate_request(request), ensure_ascii=False, allow_nan=False).encode()
        try:
            if not self.writer or self.writer.is_closing() or self.reader.at_eof():
                await self.close()
                self.reader, self.writer = await asyncio.open_connection(
                    self.HOST, 443, ssl=ssl.create_default_context(), server_hostname=self.HOST)
            header = (f'POST /v1/systemone HTTP/1.1\r\nHost: {self.HOST}\r\n'
                      f'Authorization: Bearer {self.api_key}\r\nContent-Type: application/json\r\n'
                      f'Accept: application/json\r\nContent-Length: {len(body)}\r\nConnection: keep-alive\r\n\r\n')
            send(lambda: self.writer.write(header.encode('ascii') + body))
            await self.writer.drain()
            raw = await self.reader.readuntil(b'\r\n\r\n')
            if len(raw) > 32768:
                raise DecisionError('Jev response headers exceed the limit')
            lines = raw.decode('iso-8859-1').split('\r\n')
            status = int(lines[0].split()[1])
            headers = {}
            for line in lines[1:]:
                if line:
                    name, value = line.split(':', 1)
                    if name.lower() in headers:
                        raise DecisionError('Duplicate Jev response header')
                    headers[name.lower()] = value.strip()
            if headers.get('transfer-encoding', '').lower() == 'chunked':
                chunks, total = [], 0
                while True:
                    line = await self.reader.readuntil(b'\r\n')
                    length = int(line.split(b';', 1)[0], 16)
                    if length < 0 or total + length > self.MAX_BODY:
                        raise DecisionError('Jev response exceeds the limit')
                    if not length:
                        trailer_size = 0
                        while True:
                            trailer = await self.reader.readuntil(b'\r\n')
                            trailer_size += len(trailer)
                            if trailer_size > 32768:
                                raise DecisionError('Jev trailers exceed the limit')
                            if trailer == b'\r\n':
                                break
                        break
                    chunks.append(await self.reader.readexactly(length))
                    total += length
                    if await self.reader.readexactly(2) != b'\r\n':
                        raise DecisionError('Invalid Jev response framing')
                payload = b''.join(chunks)
            elif 'content-length' in headers:
                length = int(headers['content-length'])
                if not 0 <= length <= self.MAX_BODY:
                    raise DecisionError('Jev response exceeds the limit')
                payload = await self.reader.readexactly(length)
            else:
                raise DecisionError('Jev response has no bounded body framing')
            if headers.get('connection', '').lower() == 'close':
                await self.close()
            if status != 200:
                raise ProviderError(status, retry_seconds(headers.get('retry-after')))
            try:
                return json.loads(payload)
            except (ValueError, UnicodeError):
                raise DecisionError('Jev returned invalid JSON') from None
        except BaseException:
            await self.close()
            raise


class DecisionClient:
    def __init__(self, transport, gate, settings, usage=None):
        self.transport, self.gate, self.settings = transport, gate, settings
        self.usage = usage
        self.io_lock = asyncio.Lock()

    async def ask(self, request, generation, budget, current=None, *, round_key=None):
        validate_request(request)
        for attempt in range(self.settings.retries + 1):
            self.gate.check(generation)
            remaining = budget['deadline'] - time.monotonic()
            if remaining <= 0 or budget['requests'] >= self.settings.max_requests:
                raise DecisionError('Interpretation budget exhausted; split this command')
            budget['requests'] += 1
            started = time.monotonic()
            try:
                async def exchange():
                    # Discard/correction can start a new coroutine while the old
                    # socket is cancelling. Never interleave two HTTP exchanges.
                    async with self.io_lock:
                        if current: current()
                        finish = None
                        tokens = 0

                        def send(write):
                            def guarded_write():
                                nonlocal finish
                                if current: current()
                                if time.monotonic() >= budget['deadline']:
                                    raise DecisionError('Interpretation expired before transmission')
                                if self.usage is not None:
                                    finish = self.usage.begin(round_key)
                                write()
                            self.gate.run(generation, guarded_write)

                        try:
                            response = await self.transport.exchange(request, send)
                            # A response can consume input even when its answers
                            # are invalid or the command is no longer current.
                            usage = response.get('usage') if isinstance(response, dict) else None
                            count = usage.get('input_tokens') if isinstance(usage, dict) else None
                            if type(count) is int and count > 0:
                                tokens = count
                            return response
                        finally:
                            if finish is not None:
                                finish(tokens)
                response = await asyncio.wait_for(exchange(), min(remaining, self.settings.request_seconds))
                self.gate.check(generation)
                response = validate_response(request, response)
                budget['input_tokens'] += response['usage']['input_tokens']
                budget['output_tokens'] += response['usage']['output_tokens']
                budget['last_ms'] = round((time.monotonic() - started) * 1000)
                return response
            except ProviderError as error:
                if error.status not in (408, 429, 500, 502, 503, 504):
                    raise
                delay = max(.25 * 2 ** attempt, error.retry_after)
            except (OSError, TimeoutError, asyncio.IncompleteReadError):
                delay = .25 * 2 ** attempt
            self.gate.check(generation)
            if attempt == self.settings.retries or time.monotonic() + delay >= budget['deadline']:
                raise DecisionError('Jev is unavailable or timed out; no work was accepted')
            await asyncio.sleep(delay)
