import json, os, re, subprocess, sys, time
from pathlib import Path
folder=Path('tmp/home-tests/catalog')
catalog=json.loads((folder/'catalog.json').read_text(encoding='utf-8'))
samples=[]
for extension in ['.rvt','.rfa']:
    count=0
    for entry in catalog['entries']:
        if entry['extension']!=extension or not re.match(r'^C:[\\/]',entry['path'],re.I) or not os.path.isfile(entry['path']): continue
        started=time.perf_counter()
        try:
            result=subprocess.run([sys.executable,'scripts/home-tests/catalog/probe_thumbnail.py',entry['path'],str(folder/f'shell-sample-{len(samples)}.png')],capture_output=True,text=True,timeout=12)
            status=json.loads(result.stdout) if result.stdout else {'ok':False,'error':'worker exited without result','returnCode':result.returncode}
        except subprocess.TimeoutExpired: status={'ok':False,'error':'worker stopped after 12 s timeout'}
        samples.append({'path':entry['path'],'extension':extension,'workerTotalMs':round((time.perf_counter()-started)*1000,3),**status})
        count+=1
        if count>=3: break
(folder/'thumbnail-benchmark.json').write_text(json.dumps(samples,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps([{k:v for k,v in s.items() if k!='path'} for s in samples]))
