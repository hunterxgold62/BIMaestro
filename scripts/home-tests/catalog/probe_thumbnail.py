"""Shell thumbnail only, no Revit document opening; run isolated with timeout."""
import ctypes as c, json, sys, time, uuid
from ctypes import wintypes as w
from pathlib import Path
from PIL import Image

def main():
    model, output=sys.argv[1:]
    ole=c.OleDLL('ole32'); shell=c.OleDLL('shell32'); gdi=c.WinDLL('gdi32')
    ole.CoInitialize(None)
    iid=(c.c_ubyte*16).from_buffer_copy(uuid.UUID('BCC18B79-BA16-442F-80C4-8A59C30C463B').bytes_le)
    obj=c.c_void_p(); bitmap=c.c_void_p(); started=time.perf_counter()
    shell.SHCreateItemFromParsingName.argtypes=[w.LPCWSTR,c.c_void_p,c.c_void_p,c.POINTER(c.c_void_p)]
    shell.SHCreateItemFromParsingName(model,None,c.byref(iid),c.byref(obj))
    vtbl=c.cast(obj,c.POINTER(c.POINTER(c.c_void_p))).contents
    class Size(c.Structure): _fields_=[('x',c.c_int),('y',c.c_int)]
    get=c.WINFUNCTYPE(c.c_long,c.c_void_p,Size,w.DWORD,c.POINTER(c.c_void_p))(vtbl[3])
    hr=get(obj,Size(128,128),9,c.byref(bitmap))
    if hr<0: raise OSError('GetImage HRESULT '+hex(hr&0xffffffff))
    class Bitmap(c.Structure): _fields_=[('type',w.LONG),('width',w.LONG),('height',w.LONG),('widthBytes',w.LONG),('planes',w.WORD),('bitsPixel',w.WORD),('bits',c.c_void_p)]
    class Header(c.Structure): _fields_=[('size',w.DWORD),('width',w.LONG),('height',w.LONG),('planes',w.WORD),('bitCount',w.WORD),('compression',w.DWORD),('sizeImage',w.DWORD),('x',w.LONG),('y',w.LONG),('used',w.DWORD),('important',w.DWORD)]
    bm=Bitmap(); gdi.GetObjectW.argtypes=[c.c_void_p,c.c_int,c.c_void_p]; gdi.GetObjectW(bitmap,c.sizeof(bm),c.byref(bm))
    header=Header(c.sizeof(Header),bm.width,-bm.height,1,32,0,0,0,0,0,0)
    buffer=c.create_string_buffer(bm.width*bm.height*4)
    gdi.CreateCompatibleDC.restype=c.c_void_p
    dc=gdi.CreateCompatibleDC(None); gdi.GetDIBits.argtypes=[c.c_void_p,c.c_void_p,w.UINT,w.UINT,c.c_void_p,c.c_void_p,w.UINT]
    if not gdi.GetDIBits(dc,bitmap,0,bm.height,buffer,c.byref(header),0): raise OSError('GetDIBits failed')
    Image.frombytes('RGBA',(bm.width,bm.height),buffer.raw,'raw','BGRA').save(output)
    gdi.DeleteObject.argtypes=[c.c_void_p]; gdi.DeleteObject(bitmap)
    gdi.DeleteDC.argtypes=[c.c_void_p]; gdi.DeleteDC(dc)
    c.WINFUNCTYPE(w.ULONG,c.c_void_p)(vtbl[2])(obj); ole.CoUninitialize()
    print(json.dumps({'ok':True,'width':bm.width,'height':bm.height,'elapsedMs':round((time.perf_counter()-started)*1000,3)}))

if __name__=='__main__':
    try: main()
    except Exception as error: print(json.dumps({'ok':False,'error':str(error)}))
