"""Bounded read-only CFB preview probe, follows only allocation/directory/preview chains."""
import hashlib,io,json,os,re,struct,time
from pathlib import Path
from PIL import Image
FREE=0xffffffff; END=0xfffffffe
class CFB:
    def __init__(self,path):
        self.f=open(path,'rb'); self.bytesRead=0; h=self.read(0,512)
        if h[:8]!=bytes.fromhex('d0cf11e0a1b11ae1'): raise ValueError('Not a CFB file')
        self.s=1<<struct.unpack_from('<H',h,30)[0]; self.ms=1<<struct.unpack_from('<H',h,32)[0]
        if self.s not in (512,4096):raise ValueError('Unsupported sector size')
        difat=list(struct.unpack_from('<109I',h,76)); nxt,n=struct.unpack_from('<II',h,68)
        for _ in range(min(n,4096)):
            values=struct.unpack('<'+'I'*(self.s//4),self.sector(nxt)); difat.extend(values[:-1]); nxt=values[-1]
        self.fat=[]
        for sid in difat:
            if sid<END:self.fat.extend(struct.unpack('<'+'I'*(self.s//4),self.sector(sid)))
        directory=self.chain(struct.unpack_from('<I',h,48)[0],self.fat,limit=16*1024*1024)
        self.entries=[]
        for pos in range(0,len(directory),128):
            raw=directory[pos:pos+128]; length=struct.unpack_from('<H',raw,64)[0]
            if length<2 or length>64:continue
            self.entries.append({'name':raw[:length-2].decode('utf-16-le'),'type':raw[66],'start':struct.unpack_from('<I',raw,116)[0],'size':struct.unpack_from('<Q',raw,120)[0]})
        self.root=next(e for e in self.entries if e['type']==5)
        self.cutoff=struct.unpack_from('<I',h,56)[0]; self.ministart=struct.unpack_from('<I',h,60)[0]
    def read(self,pos,length):
        self.f.seek(pos); result=self.f.read(length); self.bytesRead+=len(result); return result
    def sector(self,sid):return self.read((sid+1)*self.s,self.s)
    def chain(self,start,allocation,limit):
        output=bytearray(); seen=set(); sid=start
        while sid<END and len(output)<limit:
            if sid in seen or sid>=len(allocation):raise ValueError('Invalid sector chain')
            seen.add(sid); output.extend(self.sector(sid));sid=allocation[sid]
        return bytes(output[:limit])
    def stream(self,e):
        if e['size']>16*1024*1024:raise ValueError('Preview too large')
        if e['size']>=self.cutoff:return self.chain(e['start'],self.fat,e['size'])
        raw=self.chain(self.ministart,self.fat,1024*1024)
        mini=struct.unpack('<'+'I'*(len(raw)//4),raw)
        # Map root-stream sectors, reading only the requested mini sectors.
        roots=[]; sid=self.root['start'];seen=set()
        while sid<END:
            if sid in seen:raise ValueError('Root loop')
            seen.add(sid);roots.append(sid);sid=self.fat[sid]
        out=bytearray();sid=e['start'];seen=set()
        while sid<END and len(out)<e['size']:
            if sid in seen:raise ValueError('Mini loop')
            seen.add(sid); offset=sid*self.ms;physical=(roots[offset//self.s]+1)*self.s+offset%self.s
            out.extend(self.read(physical,self.ms));sid=mini[sid]
        return bytes(out[:e['size']])

folder=Path('tmp/home-tests/catalog'); catalog=json.loads((folder/'catalog.json').read_text(encoding='utf-8')); results=[]
for ext in ['.rvt','.rfa']:
    count=0
    for entry in catalog['entries']:
        if entry['extension']!=ext or not re.match(r'^C:[\\/]',entry['path'],re.I) or not os.path.isfile(entry['path']):continue
        before=os.stat(entry['path']);start=time.perf_counter();reader=None
        item={'path':entry['path'],'extension':ext,'ok':False}
        try:
            reader=CFB(entry['path']); item['previewStreams']=[{'name':e['name'],'size':e['size']} for e in reader.entries if 'preview' in e['name'].lower()]
            for stream in [e for e in reader.entries if e['type']==2 and 'preview' in e['name'].lower()]:
                data=reader.stream(stream)
                preview_hash=hashlib.sha256(data).hexdigest()
                for magic in [b'\x89PNG\r\n\x1a\n',b'\xff\xd8\xff',b'BM']:
                    index=data.find(magic,0,4096)
                    if index<0:continue
                    try:
                        image=Image.open(io.BytesIO(data[index:]));image.load()
                        target=folder/f'embedded-{len(results)}.png';image.save(target)
                        item.update(ok=True,thumbnailPath=str(target.resolve()),width=image.width,height=image.height,format=image.format,stream=stream['name'],imageOffset=index,sourcePreviewSha256=preview_hash,sourcePreviewHashUnchanged=preview_hash==hashlib.sha256(reader.stream(stream)).hexdigest())
                        entry['embeddedThumbnailPath']=str(target.resolve());break
                    except Exception:pass
                if item['ok']:break
        except Exception as error:item['error']=str(error)
        finally:
            if reader:item['bytesRead']=reader.bytesRead;reader.f.close()
        after=os.stat(entry['path']);item['sourceStatUnchanged']=(before.st_size,before.st_mtime_ns)==(after.st_size,after.st_mtime_ns)
        item['elapsedMs']=round((time.perf_counter()-start)*1000,3);results.append(item);count+=1
        if count>=12:break
(folder/'embedded-benchmark.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
(folder/'catalog.json').write_text(json.dumps(catalog,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps([{k:v for k,v in r.items() if k not in ('path','thumbnailPath')} for r in results]))
