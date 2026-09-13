"""Explicit Core-only cutover, preserving the original Core image/data for rollback."""
import importlib.util
import json
from pathlib import Path
import sys

spec=importlib.util.spec_from_file_location('rollout',Path(__file__).with_name('deploy-m8-core.py'))
d=importlib.util.module_from_spec(spec);spec.loader.exec_module(d)
d.RELEASE=d.ROOT/'cleanup-20260914'
d.BASE_IMAGE='sha256:c6933b9aa421f7b4f81f3ff492eb2e2ce340f32138117c43a9c723f6e127732c'
d.CORE_IMAGE='lsoverlay/backend:core-cleanup-20260914'

def compose(core,*args):
    overlay=(d.RELEASE if core else d.ROOT/'rc1')/'compose.core.yaml'
    return d.run('docker','compose','--project-directory','/srv/apps/lsoverlay','-f',str(d.COMPOSE),'-f',str(overlay),'--profile','production',*args,timeout=90)
d.compose=compose

def stop_dev():
    targets={'lsoverlay-core-readonly-bridge-1':'core-readonly-dev','lsoverlay-core-media-1':'core-readonly-dev','lsoverlay-core-dev-fixture-1':None}
    records=[]
    for name,label in targets.items():
        item=d.inspect(name);labels=item['Config']['Labels']
        if label and labels.get('ls-overlay.environment')!=label:raise RuntimeError('Unexpected dev container')
        if not label and (labels.get('com.docker.compose.project')!='lsoverlay-core-dev' or labels.get('com.docker.compose.service')!='fixture'):raise RuntimeError('Unexpected fixture')
        records.append({'name':name,'id':item['Id'],'restart':item['HostConfig']['RestartPolicy'],'wasRunning':item['State']['Running']})
    record=d.RELEASE/'retired-dev-containers.json'
    if record.exists():raise RuntimeError('Preserve previous retirement record')
    record.write_text(json.dumps(records,indent=2))
    for item in records:
        d.run('docker','update','--restart=no',item['id'])
        d.run('docker','stop','--time','15',item['id'])
    print(json.dumps({'stoppedDev':[item['name'] for item in records],'deletedFiles':0}))

if __name__=='__main__':
    try:
        d.validate_paths()
        if sys.argv[1:]==['stop-dev']:stop_dev()
        elif sys.argv[1:]==['promote']:d.promote()
        else:raise RuntimeError('Explicit stop-dev or promote required')
    except Exception as error:
        print(json.dumps({'failed':True,'errorType':type(error).__name__,'secretsLogged':False}));sys.exit(1)
