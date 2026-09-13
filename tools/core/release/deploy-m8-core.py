"""Operator-gated Core cutover. No Railway/Cloudflare calls or credential output."""
import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import time
import urllib.request

ROOT = Path('/srv/apps/lsoverlay-core-release')
RELEASE = ROOT / 'rc1'
COMPOSE = Path('/srv/apps/lsoverlay/compose.yaml')
COMPOSE_HASH = 'a3a2d878d549c6572253769ccdb3f3b9d5587ddf16b35d375f0ebb2eb3da669b'
BASE_IMAGE = 'sha256:b92549a8741a7ef16b3d9621488accf42a601e675cc4950cd2775d9d9f778b19'
BACKEND = 'lsoverlay-backend-1'
WORKER = 'lsoverlay-core-media-production-1'
KEY = ROOT / 'private/core-media-key'
CACHE = Path('/srv/cache/lsoverlay-core-production')

def run(*args, timeout=60):
    result = subprocess.run(args, capture_output=True, text=True, timeout=timeout)
    if result.returncode:
        # Docker errors/config may include environment values. Never echo them.
        raise RuntimeError('Command failed: ' + args[0])
    return result.stdout

def inspect(name):
    return json.loads(run('docker', 'inspect', name))[0]

def stamp(name):
    item=inspect(name)
    return {key:item[key] for key in ('Id','Image')} | {'started':item['State']['StartedAt'], 'health':item['State'].get('Health',{}).get('Status')}

def public_checks():
    for path in ('healthz','api/v1/core/manifest'):
        deadline=time.monotonic()+30
        while True:
            try:
                # The public edge rejects Python's default HTTP client (403)
                # even for the unchanged healthy backend. Use ordinary curl;
                # do not weaken edge rules or impersonate a browser.
                value=json.loads(run('curl','--fail','--silent','--show-error','--max-time','10','https://overlay.revo32.cloud/'+path))
                if (path=='healthz' and value.get('status')!='ok') or (path.endswith('manifest') and (value.get('readOnly') is not False or value.get('mediaDelivery') is not True)):
                    raise RuntimeError('Public health/Core contract failed')
                break
            except Exception as error:
                if time.monotonic()>=deadline:
                    raise RuntimeError('Public '+path+' failed: '+str(getattr(error,'code',type(error).__name__))) from None
                time.sleep(2)

def validate_paths():
    for path in (ROOT, RELEASE, COMPOSE, KEY, CACHE):
        if path.resolve()!=path or any(p.is_symlink() for p in (path,*path.parents)):
            raise RuntimeError('Symlink/unexpected path rejected')
    if os.getuid()!=1000 or hashlib.sha256(COMPOSE.read_bytes()).hexdigest()!=COMPOSE_HASH:
        raise RuntimeError('Production compose/user differs from reviewed baseline')

def prepare_worker():
    before=stamp(BACKEND);status=stamp('lsoverlay-status-1')
    if before['Image']!=BASE_IMAGE or before['health']!='healthy':raise RuntimeError('Unexpected Production baseline')
    if not (RELEASE/'media/LSOverlay.CoreMediaHost.dll').is_file():raise RuntimeError('Media publish missing')
    KEY.parent.mkdir(mode=0o700,parents=True,exist_ok=True)
    if not KEY.exists():
        with os.fdopen(os.open(KEY,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600),'w') as stream:stream.write(secrets.token_hex(32))
    if KEY.stat().st_uid!=1000 or KEY.stat().st_mode&0o077:raise RuntimeError('Media key permissions unsafe')
    CACHE.mkdir(mode=0o700,parents=True,exist_ok=True)
    run('docker','run','-d','--name',WORKER,'--label','ls-overlay.environment=core-production-media',
        '--read-only','--user','1000:1000','--cap-drop','ALL','--security-opt','no-new-privileges:true','--memory','512m',
        '--cpus','0.5','--pids-limit','64','--log-driver','none','--ulimit','core=0','--restart','unless-stopped',
        '--tmpfs','/tmp:rw,noexec,nosuid,size=16m','-p','127.0.0.1:15191:8080',
        '--mount',f'type=bind,src={RELEASE}/media,dst=/media,readonly',
        '--mount',f'type=bind,src={KEY},dst=/run/secrets/core-media-key,readonly',
        '--mount',f'type=bind,src={CACHE},dst=/cache','--workdir','/media',
        '-e','DOTNET_EnableDiagnostics=0','-e','ASPNETCORE_URLS=http://0.0.0.0:8080',
        '--entrypoint','dotnet',BASE_IMAGE,'/media/LSOverlay.CoreMediaHost.dll')
    if stamp(BACKEND)!=before or stamp('lsoverlay-status-1')!=status:raise RuntimeError('Production changed during worker preparation')
    print(json.dumps({'workerStarted':True,'productionBackendUnchanged':True,'loopbackPort':15191}))

def compose(core, *args):
    command=['docker','compose','--project-directory','/srv/apps/lsoverlay','-f',str(COMPOSE)]
    if core:command+=['-f',str(RELEASE/'compose.core.yaml')]
    return run(*command,'--profile','production',*args,timeout=90)

def promote():
    before=stamp(BACKEND);status=stamp('lsoverlay-status-1')
    if before['Image']!=BASE_IMAGE or before['health']!='healthy':raise RuntimeError('Unexpected Production baseline')
    image=inspect('lsoverlay/backend:core-1.0.0-rc1')['Id']
    validation=json.loads((RELEASE/'image-tests.json').read_text())
    if validation.get('passed') is not True or validation.get('image')!=image:raise RuntimeError('Exact image validation missing')
    worker=inspect(WORKER)
    media_validation=json.loads((RELEASE/'worker-validation.json').read_text())
    if media_validation.get('passed') is not True or media_validation.get('container')!=worker['Id']:raise RuntimeError('Worker validation missing')
    if worker['State']['Status']!='running' or worker['HostConfig']['PortBindings']!={'8080/tcp':[{'HostIp':'127.0.0.1','HostPort':'15191'}]}:
        raise RuntimeError('Private worker boundary differs')
    # Validate effective configuration in memory; never print interpolated secrets.
    effective=json.loads(compose(True,'config','--format','json'))['services']['backend']
    if effective['network_mode']!='host' or effective['user']!='1000:1000' or effective['environment'].get('LS_CORE_ENABLED')!='1':
        raise RuntimeError('Backend overlay does not match reviewed Core scope')
    if effective['environment'].get('LSO_TRUSTED_CLOUDFLARED_PEER')!='127.0.0.1':raise RuntimeError('Trusted proxy changed')
    data_mount=[v for v in effective['volumes'] if v['target']=='/data']
    if len(data_mount)!=1 or data_mount[0]['source']!='/srv/data/lsoverlay/backend':raise RuntimeError('Production data mount changed')
    # Stop the sole bot, take a consistent protected backup, then start only Backend.
    backup=ROOT/'backups'/time.strftime('before-core-%Y%m%d-%H%M%S')
    backup.mkdir(mode=0o700,parents=True,exist_ok=False)
    compose(False,'stop','backend')
    try:
        if inspect(BACKEND)['State']['Running']:raise RuntimeError('Previous bot still running')
        run('tar','-czf',str(backup/'backend-data.tar.gz'),'-C','/srv/data/lsoverlay/backend','.')
        os.chmod(backup/'backend-data.tar.gz',0o600)
        (backup/'deployment.json').write_text(json.dumps({'before':before,'status':status,'newImage':image}))
        os.chmod(backup/'deployment.json',0o600)
        compose(True,'up','-d','--no-deps','--no-build','backend')
        deadline=time.monotonic()+75
        while time.monotonic()<deadline:
            state=stamp(BACKEND)
            if state['Image']==image and state['health']=='healthy':break
            time.sleep(2)
        else:raise RuntimeError('Core Backend health deadline exceeded')
        public_checks()
        if stamp('lsoverlay-status-1')!=status:raise RuntimeError('Status service changed')
    except Exception as error:
        # Never restore stale data. Revert executable/config only, using the same live data.
        compose(False,'up','-d','--no-deps','--no-build','backend')
        print(json.dumps({'promoted':False,'rollbackRequested':True,'dataBackupPreserved':True,'reason':str(error) if isinstance(error,RuntimeError) else type(error).__name__}))
        raise
    result={'promoted':True,'before':before,'after':stamp(BACKEND),'statusUnchanged':True,'backup':str(backup),'publicHealthAndManifest':True}
    (RELEASE/'promotion.json').write_text(json.dumps(result,indent=2))
    print(json.dumps(result))

def main():
    validate_paths()
    if sys.argv[1:]==['prepare-worker']:prepare_worker()
    elif sys.argv[1:]==['promote']:promote()
    else:raise RuntimeError('Explicit prepare-worker or promote required')

if __name__=='__main__':
    try:main()
    except Exception as error:
        print(json.dumps({'failed':True,'errorType':type(error).__name__,'secretsLogged':False}));sys.exit(1)
