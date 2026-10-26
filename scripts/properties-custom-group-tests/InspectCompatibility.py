"""Check installed native exports and exact hashes against the production bindings.

Static checks only; use VerifyConfigurableGroups.py for actual Revit evidence.
Requires pefile. Does not load or execute Autodesk native libraries.
"""
import hashlib
import json
from pathlib import Path
import re

import pefile

pefile.MAX_IMPORT_SYMBOLS = 100000
pefile.MAX_SYMBOL_EXPORT_COUNT = 100000
repo = Path(__file__).resolve().parents[2]
source = (repo / 'BIMaestro/les couleurs/Proprietes/NativeCustomGroupRows.cs').read_text(encoding='utf-8-sig')
out = repo / 'tmp/properties-custom-group-tests/multiversion-audit'
out.mkdir(parents=True, exist_ok=True)
results = []
for year in range(2023, 2028):
    header = {
        2023: '?addGroupHeader@ElemGrid@@QEAAXPEAHVElementId@@@Z',
        2024: '?addGroupHeader@ElemGrid@@QEAAXPEAHVForgeTypeId@@@Z',
        2027: '?addGroupHeader@ElemGrid@@QEAAXV?$NotNull@PEAHX@@AEBVAString@@@Z',
    }.get(year, '?addGroupHeader@ElemGrid@@QEAAXPEAHAEBVAString@@@Z')
    required = {
        'og1100asu.dll': [
            '?InsertRows@CGXGridCore@@UEAAHKKPEAHPEAVCObArray@@1KIW4GXCmdType@@@Z',
            '?RemoveRows@CGXGridCore@@UEAAHKKIW4GXCmdType@@@Z',
        ],
        'DesktopMFC.dll': [header, '?isCellPartOfAGroupHeader@ElemGrid@@QEAAHKK@Z'],
        'Utility.dll': ['??0AString@@QEAA@PEB_W@Z', '??1AString@@QEAA@XZ'],
    }
    if year < 2025:
        required['og1100asu.dll'] += [
            '??0CGXRange@@QEAA@KKKK@Z',
            '?SetValueRange@CGXGridCore@@QEAAHAEBVCGXRange@@PEB_WW4GXModifyType@@HI@Z',
        ]
    if year == 2024:
        required['Utility.dll'] += ['??0ForgeTypeId@@QEAA@XZ']
    profile = re.search(r'case "' + str(year) + r'": hashes=new\[\]\{([^}]+)\}', source)
    assert profile, f'Missing production profile: {year}'
    hashes = re.findall(r'"([A-F0-9]{64})"', profile.group(1))
    assert len(hashes) == 3
    modules = {}
    for (name, symbols), expected in zip(required.items(), hashes):
        path = Path(f'C:/Program Files/Autodesk/Revit {year}') / name
        data = path.read_bytes()
        actual = hashlib.sha256(data).hexdigest().upper()
        assert actual == expected, f'Hash mismatch: {path}'
        pe = pefile.PE(data=data)
        exports = {s.name.decode(): s.address for s in pe.DIRECTORY_ENTRY_EXPORT.symbols if s.name}
        for symbol in symbols:
            assert symbol in exports, f'Missing export: {path} / {symbol}'
            assert symbol in source, f'Unbound export: {year} / {symbol}'
        modules[name] = {'sha256': actual, 'exports': {s: hex(exports[s]) for s in symbols}}
        pe.close()
    results.append({'year': str(year), 'modules': modules, 'pass': True})
    print(f'PASS Revit {year}: exact hashes and all required exports')
(out / 'compatibility.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
