"""Read-oriented invalid-input gate against the DEV-only media worker."""
import ipaddress
import hashlib
import json
from pathlib import Path
import subprocess
import struct
import sys
import urllib.error
import urllib.request

NAME = 'lsoverlay-core-media-1'
NETWORK = 'lsoverlay-core-readonly-bridge'
RUNTIME = 'sha256:b92549a8741a7ef16b3d9621488accf42a601e675cc4950cd2775d9d9f778b19'
KEY = Path('/srv/apps/lsoverlay-core-dev/m6/private/core-media-key')


def main():
    if sys.argv[1:] not in ([], ['--diagnostics'], ['--providers']):
        raise RuntimeError('Unsupported development verification mode')
    container = json.loads(subprocess.run(['docker', 'inspect', NAME], check=True, capture_output=True, text=True).stdout)[0]
    if (container['Name'] != '/' + NAME or container['Config']['Labels'].get('ls-overlay.environment') != 'core-readonly-dev'
            or container['Image'] != RUNTIME or container['HostConfig']['PortBindings'] or container['State']['Status'] != 'running'):
        raise RuntimeError('Isolated media scope mismatch')
    address = container['NetworkSettings']['Networks'][NETWORK]['IPAddress']
    if not ipaddress.ip_address(address).is_private:
        raise RuntimeError('Nonprivate worker address')
    origin = f'http://{address}:8080'
    secret = KEY.read_text().strip()  # never printed or passed to process arguments
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    if sys.argv[1:] == ['--providers']:
        # Public examples from the providers' own documentation, not user chat
        # URLs, signed attachments or arbitrary operator-supplied fetch targets.
        examples = (
            ('klipy', 'https://static.klipy.com/ii/d7aec6f6f171607374b2065c836f92f4/ec/f3/NIKSFkmQ.gif'),
            ('giphy', 'https://media1.giphy.com/media/cZ7rmKfFYOvYI/200.gif'),
        )
        for provider, source in examples:
            req = urllib.request.Request(origin + '/internal/media', data=json.dumps({'Source': source, 'Width': 128, 'Height': 128}).encode(), headers={'Content-Type': 'application/json', 'Authorization': 'Bearer ' + secret})
            with opener.open(req, timeout=45) as response:
                assert response.status == 200
                assert response.headers.get_content_type() == 'application/vnd.lsoverlay.media-v1'
                body = response.read(64 * 1024 * 1024 + 1)
                assert len(body) == int(response.headers['Content-Length']) and len(body) <= 64 * 1024 * 1024
                magic, width, height, count, plays = struct.unpack_from('<8siiii', body)
                assert magic == b'LSCMED1\0' and 1 <= width <= 128 and 1 <= height <= 128 and 1 < count <= 10000
                expected = 24 + count * 48
                duration = 0
                for index in range(count):
                    delay, offset, length, digest = struct.unpack_from('<iqi32s', body, 24 + index * 48)
                    assert delay >= 0 and offset == expected and length >= 8 and offset + length <= len(body)
                    assert hashlib.sha256(body[offset:offset + length]).digest() == digest
                    expected += length
                    duration += delay
                assert expected == len(body) and duration > 0
                print(json.dumps({'provider': provider, 'status': 'PASS', 'width': width, 'height': height, 'frames': count, 'bytes': len(body), 'frameHashesVerified': True, 'productionTouched': False}))
        return
    if sys.argv[1:] == ['--diagnostics']:
        request = urllib.request.Request(origin + '/internal/diagnostics', headers={'Authorization': 'Bearer ' + secret})
        with opener.open(request, timeout=3) as response:
            print(json.dumps(json.loads(response.read(8192))))  # fixed aggregate-only schema
        return
    def request(body, auth=True):
        headers = {'Content-Type': 'application/json'}
        if auth:
            headers['Authorization'] = 'Bearer ' + secret
        request = urllib.request.Request(origin + '/internal/media', data=json.dumps(body).encode(), headers=headers)
        try:
            with opener.open(request, timeout=3) as response:
                return response.status
        except urllib.error.HTTPError as error:
            return error.code
    assert request({}, False) == 401
    for source in ('http://127.0.0.1:5188/private', 'https://evil.test/image.png', 'https://cdn.discordapp.com/attachments/1/2/%2fprivate.png'):
        assert request({'Source': source, 'Width': 32, 'Height': 32}) == 422
    # Width validation happens before any allowed-origin CDN request.
    assert request({'Source': 'https://cdn.discordapp.com/emojis/123.png', 'Width': 0, 'Height': 32}) == 422
    print(json.dumps({'status': 'PASS', 'assertions': 5, 'productionTouched': False, 'cdnRequests': 0, 'secretPrinted': False}))


if __name__ == '__main__':
    main()
