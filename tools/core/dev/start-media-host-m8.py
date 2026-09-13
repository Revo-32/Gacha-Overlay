"""Create only the isolated Core media worker; no Production source/env/data."""
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys

ROOT = Path('/srv/apps/lsoverlay-core-dev/m6')
PUBLISH = ROOT / 'media-r1'
KEY = ROOT / 'private/core-media-key'
CACHE = Path('/srv/cache/lsoverlay-core-dev/media-live')
NAME = 'lsoverlay-core-media-1'
NETWORK = 'lsoverlay-core-readonly-bridge'
RUNTIME = 'sha256:b92549a8741a7ef16b3d9621488accf42a601e675cc4950cd2775d9d9f778b19'


def docker(*args):
    return subprocess.run(['docker', *args], check=True, capture_output=True, text=True, timeout=45)


def stamps():
    lines = docker('inspect', '--format', '{{.Id}} {{.Image}} {{.State.StartedAt}} {{.State.Health.Status}}', 'lsoverlay-backend-1', 'lsoverlay-status-1').stdout.splitlines()
    if len(lines) != 2 or any(not line.endswith(' healthy') for line in lines):
        raise RuntimeError('Production health precondition changed')
    return lines


def main():
    if sys.argv[1:] not in ([], ['media-r2'], ['media-r3'], ['media-r4'], ['media-r5']):
        raise RuntimeError('Only the explicitly reviewed development versions are accepted')
    publish = ROOT / (sys.argv[1] if len(sys.argv) == 2 else 'media-r1')
    if os.getuid() != 1000 or not (publish / 'LSOverlay.CoreMediaHost.dll').is_file():
        raise RuntimeError('Explicit dev publish/user required')
    for path in (publish, KEY, CACHE):
        if path.resolve() != path or any(item.is_symlink() for item in (path, *path.parents)):
            raise RuntimeError('Symlink path rejected')
    existing = subprocess.run(['docker', 'inspect', NAME], capture_output=True)
    old = json.loads(existing.stdout)[0] if existing.returncode == 0 else None
    if old is not None and (len(sys.argv) != 2 or old['Name'] != '/' + NAME or old['Image'] != RUNTIME
            or old['Config']['Labels'].get('ls-overlay.environment') != 'core-readonly-dev'
            or old['HostConfig']['NetworkMode'] != NETWORK or old['HostConfig']['PortBindings']):
        raise RuntimeError('Existing worker does not match reviewed replacement scope')
    preserved = NAME + '-before-' + publish.name
    if old is not None and subprocess.run(['docker', 'inspect', preserved], capture_output=True).returncode == 0:
        raise RuntimeError('Previous worker already preserved; refusing overwrite')
    network = json.loads(docker('network', 'inspect', NETWORK).stdout)[0]
    if network['Labels'].get('ls-overlay.environment') != 'core-readonly-dev':
        raise RuntimeError('Unexpected non-development network')
    before = stamps()
    KEY.parent.mkdir(mode=0o700, exist_ok=True)
    if not KEY.exists():
        fd = os.open(KEY, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, 'w') as stream:
            stream.write(secrets.token_hex(32))
    if KEY.stat().st_uid != 1000 or KEY.stat().st_mode & 0o077:
        raise RuntimeError('Private media key permissions rejected')
    CACHE.mkdir(parents=True, exist_ok=True, mode=0o700)
    if old is not None:
        docker('stop', '--time', '5', old['Id'])
        docker('rename', old['Id'], preserved)
    result = docker('run', '-d', '--name', NAME, '--label', 'ls-overlay.environment=core-readonly-dev',
                    '--network', NETWORK, '--network-alias', 'lsoverlay-core-media', '--read-only', '--user', '1000:1000',
                    '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true', '--memory', '512m', '--cpus', '0.5',
                    '--pids-limit', '64', '--log-driver', 'none', '--ulimit', 'core=0', '--restart', 'on-failure:3',
                    '--tmpfs', '/tmp:rw,noexec,nosuid,size=16m', '--mount', f'type=bind,src={publish},dst=/media,readonly',
                    '--mount', f'type=bind,src={KEY},dst=/run/secrets/core-media-key,readonly',
                    '--mount', f'type=bind,src={CACHE},dst=/cache', '--workdir', '/media', '--env', 'DOTNET_EnableDiagnostics=0',
                    '--env', 'ASPNETCORE_URLS=http://0.0.0.0:8080', '--entrypoint', 'dotnet', RUNTIME, '/media/LSOverlay.CoreMediaHost.dll')
    if stamps() != before:
        raise RuntimeError('Production identity/start/health changed')
    print(json.dumps({'status': 'STARTED', 'containerId': result.stdout.strip(), 'productionUnchanged': True,
                      'publishedPorts': 0, 'memoryMiB': 512, 'cacheDisposable': True, 'credentialsImported': False}))


if __name__ == '__main__':
    main()
