"""Bounded private-worker verification; no production Discord content or tokens."""
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import urllib.request
import urllib.error

root=Path('/srv/apps/lsoverlay-core-release')
worker=json.loads(subprocess.run(['docker','inspect','lsoverlay-core-media-production-1'],check=True,capture_output=True,text=True).stdout)[0]
if worker['Config']['Labels'].get('ls-overlay.environment')!='core-production-media' or worker['HostConfig']['PortBindings']!={'8080/tcp':[{'HostIp':'127.0.0.1','HostPort':'15191'}]}:
    raise RuntimeError('Worker boundary mismatch')
secret=(root/'private/core-media-key').read_text().strip()
opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
def fetch(source,auth=True):
    headers={'Content-Type':'application/json'}
    if auth:headers['Authorization']='Bearer '+secret
    request=urllib.request.Request('http://127.0.0.1:15191/internal/media',data=json.dumps({'Source':source,'Width':128,'Height':128}).encode(),headers=headers)
    return opener.open(request,timeout=50)
for source,auth,status in [('http://127.0.0.1/private',True,422),('https://example.com/not-allowed.png',True,422),('file:///etc/passwd',True,422),('https://cdn.discordapp.com/a.png',False,401)]:
    try:fetch(source,auth);raise RuntimeError('Invalid input accepted')
    except urllib.error.HTTPError as error:
        if error.code!=status:raise RuntimeError('Invalid input status mismatch')
providers=[]
for name,source in [('klipy','https://static.klipy.com/ii/d7aec6f6f171607374b2065c836f92f4/ec/f3/NIKSFkmQ.gif'),('giphy','https://media1.giphy.com/media/cZ7rmKfFYOvYI/200.gif')]:
    with fetch(source) as response:
        body=response.read(64*1024*1024+1)
        if response.status!=200 or len(body)>64*1024*1024:raise RuntimeError('Media size/status failure')
        magic,width,height,count,plays=struct.unpack_from('<8siiii',body)
        if magic!=b'LSCMED1\0' or not 1<=width<=128 or not 1<=height<=128 or not 1<count<=10000:raise RuntimeError('Media header failure')
        expected=24+count*48
        for i in range(count):
            delay,offset,length,digest=struct.unpack_from('<iqi32s',body,24+i*48)
            if delay<0 or offset!=expected or length<8 or offset+length>len(body) or hashlib.sha256(body[offset:offset+length]).digest()!=digest:raise RuntimeError('Media frame failure')
            expected+=length
        if expected!=len(body):raise RuntimeError('Media trailing bytes')
        providers.append({'provider':name,'frames':count,'bytes':len(body),'allFrameHashesVerified':True})
record={'passed':True,'container':worker['Id'],'invalidInputsRejected':4,'providers':providers,'discordWrites':0}
(root/'rc1/worker-validation.json').write_text(json.dumps(record,indent=2))
print(json.dumps(record))
