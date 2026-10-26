"""Validate actual Revit exports for configurable native custom groups."""
import json
from pathlib import Path
import sys

root=Path(sys.argv[1])
read=lambda name:json.loads((root/name).read_text(encoding="utf-8-sig"))
original=read("original.json")
results=[]
names=["created.json","report-custom-rename.json","report-custom-transfer.json","report-custom-delete.json","cleared.json"]
names += [name for name in ("report-custom-empty.json","report-custom-locked.json") if (root/name).exists()]
for name in names:
    report=read(name)
    errors=[]
    if report["parameters"]!=original["parameters"]:errors.append("model_values_changed")
    if report["schema"]!=original["schema"]:errors.append("parameter_schema_changed")
    if report["requiresRebuild"]:errors.append("uncertain_native_state")
    if "conservée dans son état" in report["status"]:errors.append("native_application_failed")
    maps=[m for m in report["mappings"] if m["row"]>0]
    if {m["id"] for m in maps}!={m["id"] for m in original["mappings"] if m["row"]>0}:errors.append("visible_parameter_set_changed")
    if any(m["expected"]!=m["actual"] for m in maps):errors.append("parameter_identity_mismatch")
    definitions=report["definitions"] or []
    headers=[r for r in report["rows"] if r["header"] and r["custom"]]
    if {r["custom"]:r["label"] for r in headers}!={g["Key"]:g["Name"] for g in definitions}:errors.append("native_headers_mismatch")
    for group in definitions:
        allowed={m["key"] for m in maps if not m.get("readOnly",False)}
        if {m["key"] for m in maps if m["custom"]==group["Key"]}!=set(group["Members"]) & allowed:errors.append("membership_mismatch:"+group["Key"])
        header=next((r for r in headers if r["custom"]==group["Key"]),None)
        if header:
            end=next((r["row"] for r in report["rows"] if r["header"] and r["row"]>header["row"]),len(report["rows"])+1)
            physical={m["key"] for m in maps if header["row"]<m["row"]<end}
            if physical!=set(group["Members"]) & allowed:errors.append("physical_group_boundary_mismatch:"+group["Key"])
    if any(m.get("readOnly",False) and m["custom"] is not None for m in maps):errors.append("readonly_member_moved")
    if name=="cleared.json" and [(r["label"],r["header"]) for r in report["rows"]]!=[(r["label"],r["header"]) for r in original["rows"]]:errors.append("original_palette_not_restored")
    results.append({"stage":name,"pass":not errors,"errors":errors,"headers":len(headers),"visibleIdentities":len(maps),"values":len(report["parameters"])})
output={"pass":all(r["pass"] for r in results),"stages":results,"scope":"Actual native exports; read-only structural and model checks, not a keyboard-entry qualification."}
(root/"configurable-groups-validation.json").write_text(json.dumps(output,indent=2,ensure_ascii=False),encoding="utf-8")
print(json.dumps(output,indent=2,ensure_ascii=False))
raise SystemExit(0 if output["pass"] else 1)
