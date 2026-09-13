"""Versioned replacement of the explicitly isolated read-only dev bridge ONLY."""
import json
import os
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path('/srv/apps/lsoverlay-core-dev/m6')
NAME = 'lsoverlay-core-readonly-bridge-1'
NETWORK = 'lsoverlay-core-readonly-bridge'
RUNTIME = 'sha256:b92549a8741a7ef16b3d9621488accf42a601e675cc4950cd2775d9d9f778b19'
LABEL = 'core-readonly-dev'


def docker(*args):
    return subprocess.run(['docker', *args], check=True, capture_output=True, text=True, timeout=45)


def stamps():
    lines = docker('inspect', '--format', '{{.Id}} {{.Image}} {{.State.StartedAt}} {{.State.Health.Status}}', 'lsoverlay-backend-1', 'lsoverlay-status-1').stdout.splitlines()
    if len(lines) != 2 or any(not line.endswith(' healthy') for line in lines):
        raise RuntimeError('Production health precondition changed')
    return lines


def main():
    if os.getuid() != 1000 or len(sys.argv) != 2 or not re.fullmatch(r'bridge-r[2-9][0-9]?', sys.argv[1]):
        raise RuntimeError('Explicit versioned dev publish directory required')
    leaf = sys.argv[1]
    publish = ROOT / leaf
    if publish.resolve() != publish or not (publish / 'LSOverlay.CoreDevBridge.dll').is_file():
        raise RuntimeError('Dev publish missing or symlinked')
    old = json.loads(docker('inspect', NAME).stdout)[0]
    if (old['Name'] != '/' + NAME or old['Image'] != RUNTIME or old['Config']['Labels'].get('ls-overlay.environment') != LABEL
            or old['HostConfig']['NetworkMode'] != NETWORK
            or old['HostConfig']['PortBindings'] != {'8080/tcp': [{'HostIp': '127.0.0.1', 'HostPort': '15190'}]}
            or any(mount['RW'] or not mount['Source'].startswith(str(ROOT) + '/') for mount in old['Mounts'] if mount['Type'] == 'bind')):
        raise RuntimeError('Refusing replacement of a nonmatching or nonisolated service')
    preserved = NAME + '-before-' + leaf
    if subprocess.run(['docker', 'inspect', preserved], capture_output=True).returncode == 0:
        raise RuntimeError('Preserved previous service exists; no overwrite')
    before = stamps()
    common = ['--read-only', '--user', '1000:1000', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true',
              '--memory', '256m', '--cpus', '0.5', '--pids-limit', '64', '--log-driver', 'none',
              '--tmpfs', '/tmp:rw,noexec,nosuid,size=16m', '--mount', f'type=bind,src={publish},dst=/bridge,readonly',
              '--workdir', '/bridge', '--env', 'DOTNET_EnableDiagnostics=0', '--entrypoint', 'dotnet']
    media = []
    if int(leaf[8:]) >= 3:
        key = ROOT / 'private/core-media-key'
        worker = json.loads(docker('inspect', 'lsoverlay-core-media-1').stdout)[0]
        if (not key.is_file() or key.resolve() != key or key.stat().st_mode & 0o077 or worker['State']['Status'] != 'running'
                or worker['Config']['Labels'].get('ls-overlay.environment') != LABEL or worker['HostConfig']['NetworkMode'] != NETWORK
                or worker['HostConfig']['PortBindings']):
            raise RuntimeError('Private media worker precondition failed')
        media = ['--mount', f'type=bind,src={key},dst=/run/secrets/core-media-key,readonly', '--env', 'CORE_DEV_MEDIA=1']
    test_name = NAME + '-test-' + leaf
    if subprocess.run(['docker', 'inspect', test_name], capture_output=True).returncode == 0:
        raise RuntimeError('Unexpected previous test instance')
    try:
        test = docker('run', '--rm', '--name', test_name, '--label', 'ls-overlay.environment=' + LABEL, '--network', 'none', *common,
                      RUNTIME, '/bridge/LSOverlay.CoreDevBridge.dll', '--self-test')
        summary = json.loads(test.stdout)
        if summary.get('status') != 'PASS':
            raise RuntimeError('Synthetic gate failed')
    finally:
        check = subprocess.run(['docker', 'inspect', test_name], capture_output=True)
        if check.returncode == 0:
            own = json.loads(check.stdout)[0]
            if own['Name'] != '/' + test_name or own['Image'] != RUNTIME or own['Config']['Labels'].get('ls-overlay.environment') != LABEL:
                raise RuntimeError('Refusing unrelated test cleanup')
            docker('rm', '-f', own['Id'])
    docker('stop', '--time', '5', old['Id'])
    docker('rename', old['Id'], preserved)
    try:
        result = docker('run', '-d', '--name', NAME, '--label', 'ls-overlay.environment=' + LABEL, '--network', NETWORK,
                        '--publish', '127.0.0.1:15190:8080', *common, '--env', 'ASPNETCORE_URLS=http://0.0.0.0:8080',
                        '--env', 'CORE_DEV_CHAT_CHANNEL=1428747924229193828', *media, RUNTIME, '/bridge/LSOverlay.CoreDevBridge.dll')
        print(json.dumps({'status': 'STARTED', 'containerId': result.stdout.strip(), 'previousPreserved': preserved, 'syntheticTests': summary}))
    finally:
        if stamps() != before:
            raise RuntimeError('Production identity/start/health changed')
    print('Production unchanged; only isolated Core development service replaced.')


if __name__ == '__main__':
    main()
