import ctypes as C
from pathlib import Path
d=C.WinDLL('C:/Program Files (x86)/Windows Kits/10/Debuggers/x64/dbghelp.dll',use_last_error=True)
k=C.WinDLL('kernel32',use_last_error=True);k.GetCurrentProcess.restype=C.c_void_p;h=k.GetCurrentProcess()
d.SymInitializeW.argtypes=[C.c_void_p,C.c_wchar_p,C.c_int];d.SymInitializeW.restype=C.c_int
d.SymLoadModuleExW.argtypes=[C.c_void_p,C.c_void_p,C.c_wchar_p,C.c_wchar_p,C.c_ulonglong,C.c_ulong,C.c_void_p,C.c_ulong];d.SymLoadModuleExW.restype=C.c_ulonglong
class SI(C.Structure):
 _fields_=[('SizeOfStruct',C.c_ulong),('TypeIndex',C.c_ulong),('Reserved',C.c_ulonglong*2),('Index',C.c_ulong),('Size',C.c_ulong),('ModBase',C.c_ulonglong),('Flags',C.c_ulong),('Value',C.c_ulonglong),('Address',C.c_ulonglong),('Register',C.c_ulong),('Scope',C.c_ulong),('Tag',C.c_ulong),('NameLen',C.c_ulong),('MaxNameLen',C.c_ulong),('Name',C.c_char*1)]
CB=C.WINFUNCTYPE(C.c_int,C.POINTER(SI),C.c_ulong,C.c_void_p)
d.SymEnumSymbols.argtypes=[C.c_void_p,C.c_ulonglong,C.c_char_p,CB,C.c_void_p];d.SymEnumSymbols.restype=C.c_int
if not d.SymInitializeW(h,str(Path('tmp').resolve()),0):raise OSError(C.get_last_error())
base=d.SymLoadModuleExW(h,None,'C:/Windows/System32/mfc140u.dll',None,0x10000000,0,None,0)
print('module',hex(base),'error',C.get_last_error())
result=[]
@CB
def collect(p,size,ctx):
 name=C.string_at(C.addressof(p.contents)+SI.Name.offset,p.contents.NameLen).decode(errors='replace');result.append((name,hex(p.contents.Address-base)));return 1
for pat in [b'*FromHandlePermanent*',b'*FromHandle@CWnd*',b'*AfxGetModuleState*']:
 print('enum',pat,d.SymEnumSymbols(h,base,pat,collect,None),C.get_last_error())
print(result)
Path('tmp/mfc-symbols.json').write_text(__import__('json').dumps(result,indent=2))
