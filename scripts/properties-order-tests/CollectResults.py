"""Summarize recorded native runs. No UI, process control, or input-file mutation.

Default is stdout only. --write explicitly refreshes the compact evidence report.
Compilation, startup and a passing write are separate pieces of evidence.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
from datetime import datetime, timezone
from pathlib import Path

SCENARIOS = {
    "text": {"id": -1010106, "expected": "ORDER_BASELINEx", "storage": "String"},
    "bool": {"id": -1001007, "expected": "0", "storage": "Integer"},
    "double": {"id": -1001108, "expected": 9 / 304.8, "storage": "Double",
               "allowed_ids": {-1001105, -1012805, -1012806}},
    "list": {"id": -1001107, "storage": "ElementId"},
}


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def source(path, root):
    return {"file": str(path.relative_to(root)).replace("\\", "/"),
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}


def parameters(report):
    # A baseline may be the plain dictionary rather than a full report.
    return report.get("parameters", report)


def parameter_id(key):
    try:
        return int(key.split("|")[1])
    except (IndexError, ValueError):
        return None


def compare(before, after):
    left, right = parameters(before), parameters(after)
    return [{"key": key, "before": left.get(key), "after": right.get(key)}
            for key in sorted(left.keys() | right.keys())
            if key not in left or key not in right or left[key] != right[key]]


def ordering_evidence(report, target_id):
    rows = report.get("rows", [])
    headers = [r for r in rows if str(r.get("marker", "")).strip() in ("^", "v", "V")]
    mapping = next((m for m in report.get("mappings", []) if m.get("id") == target_id), None)
    row_number = mapping.get("nativeRow") if mapping else None
    first = headers[0] if headers else None
    first_parameter = next((r for r in rows if first and r["row"] > first["row"]
                            and str(r.get("label", "")).strip()
                            and str(r.get("marker", "")).strip() not in ("^", "v", "V")), None)
    target_first = bool(mapping and mapping.get("matches") is True and first_parameter
                        and row_number == first_parameter["row"])
    mismatches = [m for m in report.get("mappings", []) if m.get("matches") is False]
    return {"hasApplied": report.get("hasApplied"), "firstGroup": first.get("label") if first else None,
            "targetRow": row_number, "targetFirstInFirstGroup": target_first,
            "visibleMappingMismatchCount": len(mismatches), "status": report.get("status")}


def scenario(run, kind, config, root):
    paths = [run / f"{kind}-before.json", run / f"{kind}-after.json"]
    verification_path = run / f"{kind}-verification.json"
    if kind == "list" and not verification_path.exists():
        fallback = run / "verify-write.json"
        if fallback.exists() and load(fallback).get("parameter") == "WALL_BASE_CONSTRAINT":
            verification_path = fallback
    found = [source(p, root) for p in paths if p.exists()]
    if not all(p.exists() for p in paths):
        return {"scenario": kind, "result": "not_recorded", "sources": found}
    before, after = map(load, paths)
    verification = load(verification_path) if verification_path.exists() else None
    if verification:
        found.append(source(verification_path, root))
    target_id = config["id"]
    # Read the explicit expected ElementId from the preserved verification only.
    expected = config.get("expected", verification.get("expected") if verification else None)
    deltas = compare(before, after)
    targets = [d for d in deltas if parameter_id(d["key"]) == target_id]
    failures = []
    if len(targets) != 1:
        failures.append("exactly_one_target_parameter_must_change")
    target = targets[0] if len(targets) == 1 else None
    allowed_ids = config.get("allowed_ids", set())
    target_element = target["key"].split("|")[0] if target else None
    unexpected = [d for d in deltas if not (parameter_id(d["key"]) == target_id
                  or (parameter_id(d["key"]) in allowed_ids and d["key"].split("|")[0] == target_element))]
    if unexpected:
        failures.append("unexpected_parameter_changes")
    if expected is None:
        failures.append("no_preserved_expected_value")
    elif target:
        if kind == "double":
            try:
                value_matches = math.isclose(float(target["after"]), expected, rel_tol=0, abs_tol=1e-9)
            except (ValueError, TypeError):
                value_matches = False
        else:
            value_matches = str(target["after"]) == str(expected)
        if not value_matches:
            failures.append("target_value_not_expected")
    before_order = ordering_evidence(before, target_id)
    if before_order["hasApplied"] is not True or not before_order["targetFirstInFirstGroup"]:
        failures.append("active_target_order_not_demonstrated_before_input")
    if before_order["visibleMappingMismatchCount"]:
        failures.append("native_mapping_mismatch_before_input")
    for key in ("pid", "version", "build", "path"):
        if before.get(key) != after.get(key) or not before.get(key):
            failures.append(f"inconsistent_{key}")
    if not before.get("time") or not after.get("time") or before["time"] > after["time"]:
        failures.append("invalid_record_chronology")
    return {"scenario": kind, "result": "passed_recorded_scenario" if not failures else "failed_or_incomplete",
            "expected": expected, "actual": target["after"] if target else None,
            "version": before.get("version"), "build": before.get("build"), "pid": before.get("pid"),
            "beforeTime": before.get("time"), "afterTime": after.get("time"),
            "beforeOrder": before_order, "afterHasApplied": after.get("hasApplied"),
            "afterOrderNote": "A later deselection may restore native order; the pre-input proof is checked independently.",
            "comparedParameterCount": len(parameters(before)), "deltaCount": len(deltas),
            "deltas": deltas, "unexpectedDeltaCount": len(unexpected), "failures": failures,
            "harnessVerificationPass": verification.get("pass") if verification else None, "sources": found}


def lifecycle(run, root):
    definitions = [("undo", "list-before.json", "undo-after.json"),
                   ("redo", "list-after.json", "redo-after.json"),
                   ("cancel", "cancel-before.json", "cancel-after.json")]
    tests = []
    for name, before_name, after_name in definitions:
        before_path, after_path = run / before_name, run / after_name
        if name == "cancel" and not after_path.exists():
            # The harnais writes this name for the explicit zero-delta command.
            after_path = run / "report-verify-unchanged.json"
        if not before_path.exists() or not after_path.exists():
            tests.append({"scenario": name, "result": "not_recorded"})
            continue
        before, after = load(before_path), load(after_path)
        deltas = compare(before, after)
        failures = []
        if deltas:
            failures.append("unexpected_parameter_changes")
        for key in ("pid", "version", "build", "path"):
            if not before.get(key) or before.get(key) != after.get(key):
                failures.append(f"inconsistent_{key}")
        if not before.get("time") or not after.get("time") or before["time"] > after["time"]:
            failures.append("invalid_record_chronology")
        if name == "cancel" and before.get("hasApplied") is not True:
            failures.append("active_order_not_demonstrated_before_cancel")
        tests.append({"scenario": name, "result": "passed_recorded_scenario" if not failures else "failed_or_incomplete",
                      "meaning": "All parameter values equal reference snapshot; UI action is evidenced separately.",
                      "beforeTime": before.get("time"), "afterTime": after.get("time"),
                      "comparedParameterCount": len(parameters(before)), "deltaCount": len(deltas),
                      "deltas": deltas, "failures": failures,
                      "sources": [source(before_path, root), source(after_path, root)]})
    return tests


def supplemental(run, root):
    """Read selection/restoration records without inferring undocumented UI actions."""
    definitions = [("restore", ["restore-values.json"], False),
                   ("selection_return", ["selection-return.json", "selection-wall1-settled.json"], True),
                   ("selection_wall2", ["selection-wall2-settled.json", "selection-wall2.json"], True),
                   ("selection_multiple", ["selection-multiple-settled.json", "selection-multiple.json"], False)]
    baseline_path = run / "baseline.json"
    records = []
    for name, names, applied in definitions:
        path = next((run / n for n in names if (run / n).exists()), None)
        if path is None or not baseline_path.exists():
            records.append({"scenario": name, "result": "not_recorded"})
            continue
        report, baseline = load(path), load(baseline_path)
        deltas = compare(baseline, report)
        failures = []
        if deltas:
            failures.append("unexpected_parameter_changes")
        if report.get("hasApplied") is not applied:
            failures.append("unexpected_layout_state")
        if any(m.get("matches") is False for m in report.get("mappings", [])):
            failures.append("visible_native_mapping_mismatch")
        if name == "restore":
            order_path = run / "restore-after.json"
            if not order_path.exists() or load(order_path).get("hasApplied") is not False:
                failures.append("restored_order_not_recorded")
        records.append({"scenario": name, "result": "passed_recorded_scenario" if not failures else "failed_or_incomplete",
                        "time": report.get("time"), "hasApplied": report.get("hasApplied"),
                        "comparedParameterCount": len(parameters(baseline)), "deltaCount": len(deltas),
                        "deltas": deltas, "failures": failures,
                        "sources": [source(baseline_path, root), source(path, root)]})
    return records


def collect(root):
    runs = []
    for run in sorted(root.iterdir()):
        if not run.is_dir() or not run.name[:4].isdigit():
            continue
        scenarios = [scenario(run, k, c, root) for k, c in SCENARIOS.items()] + lifecycle(run, root) + supplemental(run, root)
        runs.append({"run": run.name, "compiledArtifactPresent": (run / "BIMaestro.PropertiesOrderProbe.dll").exists(),
                     "startupRecorded": bool(list(run.glob("started-*.txt"))), "scenarios": scenarios})
    matrix = []
    for year in range(2023, 2028):
        relevant = [r for r in runs if r["run"].startswith(str(year))]
        passing = [{"run": r["run"], "scenario": s["scenario"]}
                   for r in relevant for s in r["scenarios"] if s["result"] == "passed_recorded_scenario"]
        matrix.append({"version": str(year), "compiledArtifactPresent": any(r["compiledArtifactPresent"] for r in relevant),
                       "startupRecorded": any(r["startupRecorded"] for r in relevant),
                       "passingRecordedScenarios": passing,
                       "missingRecordedScenarios": [name for name in (*SCENARIOS, "undo", "redo", "cancel")
                                                    if name not in {p["scenario"] for p in passing}],
                       "nativeWallGateEvidenceComplete": any(
                           set((*SCENARIOS, "undo", "redo", "cancel", "restore")) <=
                           {s["scenario"] for s in run["scenarios"] if s["result"] == "passed_recorded_scenario"}
                           for run in relevant),
                       "qualification": "partial_recorded_scenarios_only" if passing else "not_qualified_by_these_records"})
    layout_path = Path(__file__).resolve().parents[2] / "BIMaestro/les couleurs/Proprietes/PropertiesPaletteLayout.cs"
    layout_source = layout_path.read_text(encoding="utf-8-sig")
    gate = layout_source.split("private static bool NativeEditingQualified", 1)[1].split("#endif", 1)[0]
    enabled_versions = sorted(set(re.findall(r'version\s*==\s*"(20\d\d)"', gate.split("#else", 1)[-1])))
    disabled_versions = {}
    for entry in matrix:
        if entry["version"] not in enabled_versions:
            disabled_versions[entry["version"]] = ("Recorded wall gate scenarios complete; production gate currently disabled"
                                                   if entry["nativeWallGateEvidenceComplete"] else
                                                   "Native run started; qualification pending complete recorded scenarios"
                                                   if entry["startupRecorded"] else "No qualified native run recorded")
    return {"generatedUtc": datetime.now(timezone.utc).isoformat(), "schemaVersion": 1,
            "scope": "Recorded disposable-fixture scenarios only. Compilation/startup are not native qualification. No general release support claim.",
            "releaseScope": {"category": "cat:-2000011", "description": "Single selected walls only",
                             "enabledVersions": enabled_versions,
                             "versionsWithCompleteRecordedWallGate": [e["version"] for e in matrix if e["nativeWallGateEvidenceComplete"]],
                             "disabledVersions": disabled_versions,
                             "otherCategories": "Native order disabled; not qualified by these records"},
            "matrix": matrix, "runs": runs}


def main():
    repo = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runs-dir", type=Path, default=repo / "tmp/properties-order-tests")
    parser.add_argument("--output", type=Path, default=Path(__file__).parent / "evidence/results.json")
    parser.add_argument("--write", action="store_true", help="Explicitly refresh compact tracked evidence; otherwise stdout only")
    args = parser.parse_args()
    report = collect(args.runs_dir)
    if args.write:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"Saved {args.output}")
    print(json.dumps(report["matrix"], indent=2, ensure_ascii=False))
    for run in report["runs"]:
        for test in run["scenarios"]:
            if test["result"] != "not_recorded":
                print(run["run"], test["scenario"], test["result"], ",".join(test["failures"]))


if __name__ == "__main__":
    main()
