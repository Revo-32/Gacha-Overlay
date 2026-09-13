"""Run Core socket/security tests against the exact built M8 image assemblies."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys

ROOT=Path('/srv/apps/lsoverlay-core-release/rc1')
IMAGE='lsoverlay/backend:core-1.0.0-rc1'
if sys.argv[1:]==['--core-cleanup']:
    ROOT=Path('/srv/apps/lsoverlay-core-release/cleanup-20260914')
    IMAGE='lsoverlay/backend:core-cleanup-20260914'
elif sys.argv[1:]:raise RuntimeError('Unknown image verification profile')
SDK='mcr.microsoft.com/dotnet/sdk@sha256:5ef85cc12cb25be6ec319a7392d1e9efd53c3bc8abb971c53d8058a473f09053'

def run(*args):
    result=subprocess.run(args,capture_output=True,text=True,timeout=90)
    if result.returncode:raise RuntimeError('Image validation command failed')
    return result.stdout

image=json.loads(run('docker','inspect',IMAGE))[0]['Id']
target=ROOT/('tests-image-'+image.split(':')[-1][:12] if sys.argv[1:] else 'tests-image')
if target.exists():raise RuntimeError('Preserve previous image-test output')
shutil.copytree(ROOT/'tests-final',target)
container=run('docker','create','--network','none','--read-only',IMAGE).strip()
if len(container)!=64 or any(c not in '0123456789abcdef' for c in container):raise RuntimeError('Unexpected export container ID')
assemblies=['LSOverlay.Backend.dll','LSOverlay.Protocol.dll','LSOverlay.CoreMedia.dll','LSOverlay.RemoteClient.dll','GachaOverlay.Core.dll']
try:
    for assembly in assemblies:run('docker','cp',container+':/app/'+assembly,str(target/assembly))
finally:run('docker','rm',container) # Only the stopped export container created above.
output=run('docker','run','--rm','--network','none','--read-only','--user','1000:1000','--cap-drop','ALL',
    '--security-opt','no-new-privileges:true','--tmpfs','/tmp:rw,nosuid,size=128m','--memory','512m','--cpus','1','--pids-limit','128',
    '-e','DOTNET_CLI_HOME=/tmp','-e','DOTNET_EnableDiagnostics=0','--mount',f'type=bind,src={target},dst=/tests,readonly',
    SDK,'dotnet','vstest','/tests/GachaOverlay.Tests.dll','--TestCaseFilter:FullyQualifiedName~CoreProduction')
if 'Passed!' not in output or 'Failed:     0' not in output:raise RuntimeError('Test success output missing')
record={'passed':True,'image':image,'assemblies':{name:hashlib.sha256((target/name).read_bytes()).hexdigest() for name in assemblies},'output':output}
(ROOT/'image-tests.json').write_text(json.dumps(record,indent=2))
print(json.dumps(record))
