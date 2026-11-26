"""Start a packed standalone server the way someone who downloaded it would, and use it.

Usage: smoke-binary.py <archive> <version>

Checks the archive's contents and checksum, then runs the executable from it: refusing to start
without settings, over stdio and over HTTP against a stand-in LibreNMS on this machine. Needs
nothing but Python, so it's the same on every platform. All data is synthetic.
"""
import hashlib
import http.client
import http.server
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tarfile
import tempfile
import threading
import time
import zipfile

TOKEN = 'synthetic-librenms-token'
HTTP_TOKEN = 'synthetic-http-token-0123456789abcdef'
SETTINGS = ('LIBRENMS_', 'UNIMUS_', 'GRAYLOG_', 'MCP_', 'AUDIT_', 'DASHYNMS_', 'DOTNET_')


class LibreNms(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        body = {'status': 'ok', 'system': [{'local_ver': '26.10.0'}]} if self.path.startswith('/api/v0/system') \
            else {'status': 'ok', 'devices': [{'device_id': 1, 'hostname': 'core-sw1.example.net', 'status': 1}]}
        data = json.dumps(body).encode()
        self.send_response(200 if self.headers.get('X-Auth-Token') == TOKEN else 401)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *args):
        pass


def unpack(archive, into):
    expected = (archive.parent / f'{archive.name}.sha256').read_text(encoding='utf-8').split()
    assert expected == [hashlib.sha256(archive.read_bytes()).hexdigest(), archive.name], 'The checksum does not match.'
    if archive.suffix == '.zip':
        with zipfile.ZipFile(archive) as packed:
            # ZipFile sanitises absolute/parent paths; tar uses the data filter below.
            packed.extractall(into)  # nosec B202
    else:
        with tarfile.open(archive) as packed:
            packed.extractall(into, filter='data')
    (folder,) = into.iterdir()
    executable = folder / ('dashynms-mcp.exe' if os.name == 'nt' else 'dashynms-mcp')
    for file in (executable, folder / 'LICENSE', folder / 'NOTICES.md', folder / 'BUILD.txt', folder / 'third-party/inventory.json'):
        assert file.is_file() and file.stat().st_size > 0, f'{file.name} is missing.'
    for pack in ('microsoft.netcore.app', 'microsoft.aspnetcore.app'):
        (notices,) = (folder / 'third-party').glob(f'{pack}.runtime.*')
        assert (notices / 'THIRD-PARTY-NOTICES.TXT').stat().st_size > 0, f'{pack} has no notices.'
    assert os.name == 'nt' or os.access(executable, os.X_OK), 'It cannot be run.'
    return executable


def environment(settings):
    clean = {key: value for key, value in os.environ.items() if not key.upper().startswith(SETTINGS)}
    return clean | settings


def free_port():
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        return probe.getsockname()[1]


def refuses_without_settings(executable):
    run = subprocess.run([executable], env=environment({}), stdin=subprocess.DEVNULL, capture_output=True, timeout=120)
    assert run.returncode == 2, f'Exit code {run.returncode}: {run.stderr.decode()}'
    assert b'LIBRENMS_URL is not set' in run.stderr and run.stdout == b'', run.stderr.decode()


def answers_over_stdio(executable, librenms, version):
    server = subprocess.Popen([executable], env=environment({'LIBRENMS_URL': librenms, 'LIBRENMS_TOKEN': TOKEN}),
                              stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding='utf-8')
    errors = []
    reader = threading.Thread(target=lambda: errors.extend(server.stderr), daemon=True)
    reader.start()
    stop = threading.Timer(120, server.kill)
    stop.start()

    def request(number, method, parameters):
        server.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': number, 'method': method, 'params': parameters}) + '\n')
        server.stdin.flush()
        for line in server.stdout:
            # Anything that isn't a message would break a client.
            message = json.loads(line)
            if message.get('id') == number:
                assert 'result' in message, message
                return message['result']
        raise AssertionError('The server stopped: ' + ''.join(errors))

    try:
        started = request(1, 'initialize', {'protocolVersion': '2025-06-18', 'capabilities': {}, 'clientInfo': {'name': 'smoke', 'version': '1'}})
        assert started['serverInfo']['name'] == 'dashynms', started
        assert started['serverInfo']['version'] == version.split('-')[0], started['serverInfo']
        server.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        server.stdin.flush()
        tools = request(2, 'tools/list', {})['tools']
        assert len(tools) >= 24 and all(tool['outputSchema']['type'] == 'object' for tool in tools), tools
        answer = request(3, 'tools/call', {'name': 'find_device', 'arguments': {'query': 'core'}})
        assert answer['structuredContent'] == json.loads(answer['content'][0]['text']), answer
        assert answer['structuredContent']['data']['devices'][0]['hostname'] == 'core-sw1.example.net', answer
        assert request(4, 'prompts/list', {})['prompts'], 'No prompts.'
    finally:
        stop.cancel()
        server.kill()
        server.wait()
        reader.join(10)
    assert not any(TOKEN in line for line in errors), 'The token is in the log.'


def answers_over_http(executable, librenms):
    port = free_port()
    server = subprocess.Popen(
        [executable], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
        env=environment({'LIBRENMS_URL': librenms, 'LIBRENMS_TOKEN': TOKEN, 'MCP_TRANSPORT': 'http',
                         'MCP_HTTP_PORT': str(port), 'MCP_HTTP_TOKENS': f'smoke:{HTTP_TOKEN}'}))

    def ask(method, path, body=None, token=None):
        connection = http.client.HTTPConnection('127.0.0.1', port, timeout=60)
        headers = {'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream'}
        if token:
            headers['Authorization'] = f'Bearer {token}'
        connection.request(method, path, body and json.dumps(body), headers)
        response = connection.getresponse()
        return response.status, response.read().decode()

    try:
        for attempt in range(120):
            assert server.poll() is None, server.stderr.read().decode()
            try:
                if ask('GET', '/health') == (200, 'ok'):
                    break
            except OSError:
                pass
            time.sleep(0.5)
        else:
            raise AssertionError('It never answered.')
        assert ask('GET', '/ready') == (200, 'ready')
        hello = {'jsonrpc': '2.0', 'id': 1, 'method': 'initialize',
                 'params': {'protocolVersion': '2025-06-18', 'capabilities': {}, 'clientInfo': {'name': 'smoke', 'version': '1'}}}
        assert ask('POST', '/mcp', hello)[0] == 401
        assert ask('POST', '/mcp', hello, 'wrong')[0] == 401
        status, answer = ask('POST', '/mcp', hello, HTTP_TOKEN)
        assert status == 200 and '"name":"dashynms"' in answer, (status, answer)
    finally:
        server.kill()
        server.wait()


if __name__ == '__main__':
    archive, version = Path(sys.argv[1]).resolve(), sys.argv[2]
    stub = http.server.ThreadingHTTPServer(('127.0.0.1', 0), LibreNms)
    threading.Thread(target=stub.serve_forever, daemon=True).start()
    librenms = f'http://127.0.0.1:{stub.server_address[1]}'
    # On Windows the executable can stay locked for a moment after it's stopped.
    with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as directory:
        executable = unpack(archive, Path(directory))
        refuses_without_settings(executable)
        print('Refuses to start without settings.')
        answers_over_stdio(executable, librenms, version)
        print('Answers over stdio.')
        answers_over_http(executable, librenms)
        print('Answers over HTTP.')
