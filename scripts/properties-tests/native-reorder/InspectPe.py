import struct,uuid,json,sys
from pathlib import Path
class PE:
 def __init__(self,path):
  self.path=path;self.b=Path(path).read_bytes();self.pe=self.u32(0x3c);self.nsec=self.u16(self.pe+6);self.opt=self.pe+24;self.sopt=self.u16(self.pe+20);self.is64=self.u16(self.opt)==0x20b;self.dd=self.opt+(112 if self.is64 else 96);self.secs=[]
  for i in range(self.nsec):
   p=self.opt+self.sopt+i*40;self.secs.append((self.u32(p+12),self.u32(p+8),self.u32(p+20),self.u32(p+16)))
 def u16(self,p):return struct.unpack_from('<H',self.b,p)[0]
 def u32(self,p):return struct.unpack_from('<I',self.b,p)[0]
 def off(self,rva):
  for v,s,p,raw in self.secs:
   if v<=rva<v+max(s,raw):return p+rva-v
  return rva
 def z(self,p):return self.b[p:self.b.index(0,p)].decode(errors='replace')
 def exports(self):
  p=self.off(self.u32(self.dd));base=self.u32(p+16);n=self.u32(p+24);names=self.off(self.u32(p+32));ords=self.off(self.u32(p+36));func=self.off(self.u32(p+28));return [(self.z(self.off(self.u32(names+i*4))),base+self.u16(ords+i*2),self.u32(func+self.u16(ords+i*2)*4)) for i in range(n)]
 def debug(self):
  rva=self.u32(self.dd+6*8);sz=self.u32(self.dd+6*8+4);out=[]
  if not rva:return out
  for i in range(sz//28):
   p=self.off(rva)+28*i
   if self.u32(p+12)==2:
    d=self.u32(p+24)
    if self.b[d:d+4]==b'RSDS':out.append({'pdb':self.z(d+24),'key':uuid.UUID(bytes_le=self.b[d+4:d+20]).hex.upper()+str(self.u32(d+20))})
  return out
if __name__=='__main__':
 for path in sys.argv[1:]:
  p=PE(path);print(path);print(json.dumps(p.debug()));print('\n'.join(str(x) for x in p.exports() if any(t in x[0] for t in ['MoveRows','GetRowCount','GetParam','GetGrid','GetSafeHwnd','GetThisClass@CGXGrid','FromHandle','RuntimeClass'])) )
