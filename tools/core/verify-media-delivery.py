"""Synthetic-only WinHTTP media framing/cancellation gate; never uses live auth."""
import http.server
import pathlib
import struct
import subprocess
import sys
import threading
import time


def main():
    executable, fixture, output = map(pathlib.Path, sys.argv[1:])
    body = fixture.read_bytes()
    width, height = struct.unpack_from('<II', body, 8)
    if body[:8] != b'LSCMED1\0' or len(body) > 16 * 1024 * 1024:
        raise RuntimeError('Small synthetic media package required')

    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            pieces = self.path.split('/')
            if len(pieces) != 8 or pieces[1:5] != ['api', 'v1', 'core', 'media'] or self.headers.get('Authorization') != 'Bearer lso_' + 'a' * 43:
                self.send_error(403)
                return
            scenario = int(pieces[5][-1], 16)
            if scenario == 10:
                time.sleep(1)
            if scenario in (1, 9):
                self.send_response(302 if scenario == 1 else 403)
                self.send_header('Location', 'http://127.0.0.1:1/never-follow')
                self.end_headers()
                return
            self.send_response(200)
            self.send_header('Content-Type', 'text/plain' if scenario == 2 else 'application/vnd.lsoverlay.media-v1')
            key = pieces[5][0] * 64 if pieces[5][0] in 'cde' else 'b' * 64
            self.send_header('X-Core-Media-Key', key if scenario != 8 else '../bad')
            if scenario == 3:
                self.send_header('X-Core-Media-Key', 'b' * 64)
            self.send_header('X-Core-Media-Width', str(8193 if scenario == 6 else 0 if scenario == 7 else width))
            self.send_header('X-Core-Media-Height', str(height))
            self.send_header('X-Core-Cache-Hit', '1')
            self.send_header('Content-Length', str(512 * 1024 * 1024 + 1 if scenario == 5 else len(body)))
            try:
                self.end_headers()
                self.wfile.write(body[:30] if scenario == 4 else body)
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                pass

    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler)
    worker = threading.Thread(target=server.serve_forever, daemon=True)
    worker.start()
    try:
        result = subprocess.run([str(executable), f'http://127.0.0.1:{server.server_port}', str(output.resolve())], timeout=30)
        if result.returncode:
            raise RuntimeError('Native media delivery gate failed')
    finally:
        server.shutdown()
        server.server_close()
        worker.join()


if __name__ == '__main__':
    main()
