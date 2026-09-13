import json
import urllib.request
import urllib.error
opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
for origin in ('http://127.0.0.1:5188','https://overlay.revo32.cloud'):
    for path in ('healthz','api/v1/core/manifest'):
        try:
            with opener.open(origin+'/'+path,timeout=10) as response:
                print(json.dumps({'origin':origin,'path':path,'status':response.status,'cache':response.headers.get('CF-Cache-Status'),'body':json.loads(response.read(8192))}))
        except urllib.error.HTTPError as error:
            print(json.dumps({'origin':origin,'path':path,'status':error.code,'cache':error.headers.get('CF-Cache-Status')}))
