"""Isolated Core developer service. No production env/config/data is imported.

Only an existing image's .NET runtime is reused with an explicit new entrypoint.
Source DLLs are uploaded separately. Every mount/port/name is dev-only.
"""
import json
import os
from pathlib import Path
import socket
import subprocess

ROOT = Path('/srv/apps/lsoverlay-core-dev/m6')
NAME = 'lsoverlay-core-readonly-bridge-1'
NETWORK = 'lsoverlay-core-readonly-bridge'
RUNTIME = 'sha256:b92549a8741a7ef16b3d9621488accf42a601e675cc4950cd2775d9d9f778b19'
PRODUCTION = ('lsoverlay-backend-1', 'lsoverlay-status-1')


def docker(*args):
    return subprocess.run(['docker', *args], check=True, capture_output=True, text=True)


def stamps():
    lines = docker('inspect', '--format', '{{.Id}} {{.Image}} {{.State.StartedAt}} {{.State.Status}} {{.State.Health.Status}}', *PRODUCTION).stdout.splitlines()
    if len(lines) != 2 or any(not line.endswith(' running healthy') for line in lines):
        raise RuntimeError('Production precondition changed')
    return lines


def main():
    if os.getuid() != 1000 or ROOT.resolve() != ROOT or not (ROOT / 'bridge/LSOverlay.CoreDevBridge.dll').is_file():
        raise RuntimeError('Explicit isolated dev publish directory/user required')
    for item in (ROOT, ROOT / 'bridge', *ROOT.parents):
        if item.is_symlink():
            raise RuntimeError('Symlink path rejected')
    if subprocess.run(['docker', 'container', 'inspect', NAME], capture_output=True).returncode == 0:
        raise RuntimeError('Existing dev service preserved; inspect before replacing')
    with socket.socket() as check:
        check.bind(('127.0.0.1', 15190))
    before = stamps()
    # Build tools/runtime only: no mounted Production state, env, credentials or
    # Docker socket. Synthetic self-tests run without ANY networking first.
    common = ['--read-only', '--user', '1000:1000', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true',
              '--memory', '256m', '--cpus', '0.5', '--pids-limit', '64', '--log-driver', 'none',
              '--tmpfs', '/tmp:rw,noexec,nosuid,size=16m', '--mount', f'type=bind,src={ROOT / "bridge"},dst=/bridge,readonly',
              '--workdir', '/bridge', '--env', 'DOTNET_EnableDiagnostics=0', '--entrypoint', 'dotnet']
    test = docker('run', '--rm', '--network', 'none', *common, RUNTIME, '/bridge/LSOverlay.CoreDevBridge.dll', '--self-test')
    summary = json.loads(test.stdout)
    if summary.get('status') != 'PASS':
        raise RuntimeError('Bridge synthetic tests failed')
    if subprocess.run(['docker', 'network', 'inspect', NETWORK], capture_output=True).returncode == 0:
        raise RuntimeError('Unexpected existing dev network; inspect before reuse')
    docker('network', 'create', '--label', 'ls-overlay.environment=core-readonly-dev', NETWORK)
    result = docker('run', '-d', '--name', NAME, '--label', 'ls-overlay.environment=core-readonly-dev',
                    '--network', NETWORK, '--publish', '127.0.0.1:15190:8080', *common,
                    '--env', 'ASPNETCORE_URLS=http://0.0.0.0:8080', '--env', 'ASPNETCORE_ENVIRONMENT=Development',
                    '--env', 'CORE_DEV_CHAT_CHANNEL=1428747924229193828', RUNTIME, '/bridge/LSOverlay.CoreDevBridge.dll')
    if stamps() != before:
        raise RuntimeError('Production identity/start/health changed')
    print(json.dumps({'status': 'STARTED', 'containerId': result.stdout.strip(), 'syntheticTests': summary,
                      'loopbackPort': 15190, 'productionUnchanged': True, 'credentialsImported': False}))


if __name__ == '__main__':
    main()
