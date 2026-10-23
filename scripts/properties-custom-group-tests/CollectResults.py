"""Read-only evidence checks for a native custom-group prototype, Revit 2025.

No UI/native calls. No result is inferred from a successful build or an export alone.
"""
import argparse
from collections import Counter
from datetime import datetime
import hashlib
import json
from pathlib import Path
import re
import unicodedata

STAGES = ("baseline", "inserted", "grouped", "collapsed", "expanded", "restored")
REQUIRED_KINDS = {"text", "boolean", "double", "elementId", "neighbor", "cancel", "undo", "redo", "rollback", "active-editor-refusal"}
GROUP_MARKERS = {"^", "v", "V"}


def normal(text):
    return "".join(c for c in unicodedata.normalize("NFD", str(text)) if not c.isspace() and not unicodedata.combining(c)).upper()


def is_header(row):
    return str(row.get("marker", "")).strip() in GROUP_MARKERS


def cell(row):
    return tuple(str(row.get(k, "")) for k in ("label", "value", "marker"))


def differences(before, after):
    return [{"key": k, "before": before.get(k), "after": after.get(k)}
            for k in sorted(before.keys() | after.keys())
            if k not in before or k not in after or before[k] != after[k]]


def visible_maps(report):
    return [m for m in report.get("mappings", []) if isinstance(m.get("nativeRow"), int) and m["nativeRow"] > 0]


def editor_state(report):
    state = report.get("nativeState", {})
    buffers = [(str(w.get("className", "")), str(w.get("text", ""))) for w in state.get("children", [])
               if "EDIT" in str(w.get("className", "")).upper() and w.get("visible") is not False]
    return state.get("activeCell"), state.get("currentRow"), state.get("currentColumn"), buffers


def validate_report(report, manifest):
    errors = []
    for key, expected in (("version", "2025"), ("pid", manifest.get("pid")), ("path", manifest.get("fixturePath"))):
        if report.get(key) != expected or expected is None:
            errors.append("identity_" + key)
    try:
        datetime.fromisoformat(report["time"].replace("Z", "+00:00"))
    except (KeyError, TypeError, ValueError):
        errors.append("missing_or_invalid_time")
    rows = report.get("rows", [])
    indices = [r.get("row") for r in rows]
    if indices != list(range(1, len(rows) + 1)):
        errors.append("noncontiguous_row_indices")
    maps = visible_maps(report)
    if len({m.get("id") for m in maps}) != len(maps) or len({m["nativeRow"] for m in maps}) != len(maps):
        errors.append("duplicate_native_identity_or_row")
    by_row = {r["row"]: r for r in rows}
    for mapping in maps:
        row = by_row.get(mapping["nativeRow"])
        if row is None or is_header(row) or mapping.get("matches") is not True or normal(row["label"]) != normal(mapping.get("expectedLabel", "")):
            errors.append("invalid_mapping_" + str(mapping.get("id")))
    if not isinstance(report.get("parameters"), dict) or not report.get("parameters"):
        errors.append("parameter_snapshot_missing")
    schema = report.get("parameterSchema")
    if not isinstance(schema, dict) or not schema or set(schema) != set(report.get("parameters", {})):
        errors.append("complete_parameter_schema_missing")
    elif any(not isinstance(value, dict) or not {"storage", "readOnly", "groupTypeId"} <= value.keys() for value in schema.values()):
        errors.append("parameter_schema_descriptors_incomplete")
    return errors


def group_state(report, manifest, require_members=True):
    errors = []
    rows = report.get("rows", [])
    headers = [r for r in rows if r.get("label", "").strip() == manifest.get("headerLabel")]
    if len(headers) != 1:
        return ["custom_header_not_unique"]
    header = headers[0]
    if not is_header(header) or header.get("editable") is not False:
        errors.append("custom_header_not_native_readonly_group")
    native_test = report.get("nativeGroupHeaderTest", {})
    if header.get("isNativeGroupHeader") is not True and not (native_test.get("row") == header["row"] and native_test.get("result") is True):
        errors.append("native_group_header_api_test_missing")
    next_header = next((r["row"] for r in rows if r["row"] > header["row"] and is_header(r)), len(rows) + 1)
    ids = {m["id"] for m in visible_maps(report) if header["row"] < m["nativeRow"] < next_header}
    if require_members and ids != set(manifest.get("memberParameterIds", [])):
        errors.append("custom_group_membership_mismatch")
    return errors


def baseline_source_groups(report, member_ids):
    owners = {}
    current = None
    id_by_row = {m["nativeRow"]: m["id"] for m in visible_maps(report)}
    for row in report.get("rows", []):
        if is_header(row):
            current = row["label"]
        if id_by_row.get(row["row"]) in member_ids:
            owners[id_by_row[row["row"]]] = current
    return owners


def structural_checks(manifest, snapshots):
    results = []
    baseline = snapshots.get("baseline")
    for stage in STAGES:
        report = snapshots.get(stage)
        if report is None or baseline is None:
            results.append({"stage": stage, "result": "not_recorded"})
            continue
        errors = validate_report(report, manifest)
        if report.get("parameters") != baseline.get("parameters"):
            errors.append("model_values_changed")
        if report.get("parameterSchema") != baseline.get("parameterSchema"):
            errors.append("parameter_definitions_changed")
        if report.get("time", "") < baseline.get("time", ""):
            errors.append("capture_precedes_baseline")
        if not baseline.get("build") or report.get("build") != baseline["build"]:
            errors.append("host_build_changed_or_missing")
        baseline_ids = {m["id"] for m in visible_maps(baseline)}
        if stage == "baseline" and not set(manifest.get("memberParameterIds", [])) <= baseline_ids:
            errors.append("baseline_members_not_visible_in_native_palette")
        if {m["id"] for m in visible_maps(report)} != baseline_ids:
            errors.append("native_parameter_identity_set_changed")
        if stage not in ("baseline", "restored"):
            errors += group_state(report, manifest, stage != "inserted")
            if len(report.get("rows", [])) != len(baseline.get("rows", [])) + manifest.get("insertedRowCount", 0):
                errors.append("unexpected_inserted_row_count")
            original = Counter(cell(r) for r in baseline.get("rows", []))
            remaining = Counter(cell(r) for r in report.get("rows", []) if r.get("label", "").strip() != manifest.get("headerLabel"))
            missing, extra = original - remaining, remaining - original
            if missing or sum(extra.values()) != manifest.get("insertedRowCount", 0) - 1:
                errors.append("original_cells_not_preserved")
            if any(any(s.strip() for s in values) for values in extra):
                errors.append("unexpected_nonempty_inserted_cell")
        elif stage == "restored" and [cell(r) for r in report.get("rows", [])] != [cell(r) for r in baseline.get("rows", [])]:
            errors.append("original_row_structure_not_restored")
        if stage == "restored" and {m["id"]: m["nativeRow"] for m in visible_maps(report)} != {m["id"]: m["nativeRow"] for m in visible_maps(baseline)}:
            errors.append("original_parameter_positions_not_restored")
        if stage in ("collapsed", "expanded") and snapshots.get("grouped"):
            grouped = snapshots["grouped"]
            maps = {m["id"]: m["nativeRow"] for m in visible_maps(report)}
            grouped_maps = {m["id"]: m["nativeRow"] for m in visible_maps(grouped)}
            rows = {r["row"]: r for r in report.get("rows", [])}
            grouped_rows = {r["row"]: r for r in grouped.get("rows", [])}
            for ident, old_row in grouped_maps.items():
                new_rect = rows.get(maps.get(ident), {}).get("rect", {})
                old_rect = grouped_rows[old_row].get("rect", {})
                new_height = new_rect.get("bottom", 0) - new_rect.get("top", 0)
                old_height = old_rect.get("bottom", 0) - old_rect.get("top", 0)
                expected_height = 0 if stage == "collapsed" and ident in manifest.get("memberParameterIds", []) else old_height
                if new_height != expected_height or (ident in manifest.get("memberParameterIds", []) and old_height <= 0):
                    errors.append("unexpected_member_visibility_" + str(ident))
        results.append({"stage": stage, "result": "passed" if not errors else "failed", "errors": sorted(set(errors))})
    return results


def evaluate(manifest, snapshots, interactions):
    errors = []
    members = manifest.get("memberParameterIds", [])
    if manifest.get("version") != "2025" or not isinstance(manifest.get("pid"), int):
        errors.append("manifest_wrong_host")
    if len(set(members)) != len(members) or len(members) < 2:
        errors.append("at_least_two_unique_members_required")
    if manifest.get("insertedRowCount") not in (1, 2):
        errors.append("explicit_header_spacer_row_count_required")
    if manifest.get("nativeInsertionVerified") is not True or not any("addGroupHeader" in e and "ElemGrid" in e for e in manifest.get("operationExports", [])):
        errors.append("native_insertion_provenance_missing")
    hashes = manifest.get("moduleHashes", {})
    if not hashes or not all(isinstance(v, str) and re.fullmatch("[0-9A-Fa-f]{64}", v) for v in hashes.values()):
        errors.append("native_module_hashes_missing")
    baseline = snapshots.get("baseline")
    if baseline:
        owners = baseline_source_groups(baseline, set(members))
        if set(owners) != set(members) or None in owners.values() or len(set(owners.values())) < 2:
            errors.append("cross_group_sources_not_demonstrated")
        if any(r.get("label", "").strip() == manifest.get("headerLabel") for r in baseline.get("rows", [])):
            errors.append("custom_header_already_present_in_baseline")
    structural = structural_checks(manifest, snapshots)
    tests = []
    for item in interactions:
        before, after = item.get("beforeReport"), item.get("afterReport")
        if before is None or after is None:
            tests.append({"name": item.get("name"), "kind": item.get("kind"), "result": "not_recorded"})
            continue
        failures = validate_report(before, manifest) + validate_report(after, manifest)
        if before.get("time", "") > after.get("time", "") or before.get("build") != after.get("build"):
            failures.append("interaction_chronology_or_build_mismatch")
        if before.get("parameterSchema") != after.get("parameterSchema"):
            failures.append("parameter_definitions_changed")
        deltas = differences(before.get("parameters", {}), after.get("parameters", {}))
        expected = item.get("expectedChanges", {})
        if set(d["key"] for d in deltas) != set(expected):
            failures.append("unexpected_model_delta_set")
        for change in deltas:
            if change["key"] not in expected:
                continue
            actual, target = change["after"], expected[change["key"]]
            try:
                matches = abs(float(actual) - float(target)) <= item.get("tolerance", 0) if item.get("kind") == "double" else actual == target
            except (TypeError, ValueError):
                matches = False
            if not matches:
                failures.append("unexpected_value_" + change["key"])
        if item.get("kind") in ("text", "boolean", "double", "elementId", "neighbor"):
            failures += group_state(before, manifest)
            if not expected:
                failures.append("write_test_has_no_expected_change")
            key = item.get("targetKey")
            schema = before.get("parameterSchema", {}).get(key, {})
            required_type = {"text": "String", "boolean": "Integer", "double": "Double", "elementId": "ElementId"}.get(item["kind"])
            if key not in expected or key not in before.get("parameters", {}):
                failures.append("explicit_target_key_missing")
            if required_type and schema.get("storage") != required_type:
                failures.append("editor_storage_type_not_demonstrated")
            if schema.get("readOnly") is not False:
                failures.append("target_not_editable_in_api_schema")
            try:
                ident = int(key.split("|")[1])
                if (ident in members) != (item["kind"] != "neighbor"):
                    failures.append("target_not_in_expected_group_or_neighbor")
            except (ValueError, AttributeError, IndexError):
                failures.append("invalid_target_identity_key")
        if item.get("kind") in ("cancel", "rollback", "active-editor-refusal") and deltas:
            failures.append("operation_must_not_write_model")
        if item.get("kind") in ("undo", "redo") and not expected:
            failures.append("undo_redo_requires_an_actual_expected_value_change")
        if item.get("kind") in ("rollback", "active-editor-refusal") and [cell(r) for r in before.get("rows", [])] != [cell(r) for r in after.get("rows", [])]:
            failures.append("grid_structure_not_restored_or_untouched")
        if item.get("kind") == "active-editor-refusal":
            old, new = editor_state(before), editor_state(after)
            if old[0] not in (1, True) or not old[3] or old != new:
                failures.append("active_editor_buffer_not_proven_untouched")
        if item.get("kind") == "rollback":
            failure_report = item.get("failureReport")
            if failure_report is None:
                failures.append("intermediate_mutation_before_rollback_not_recorded")
            else:
                failures += validate_report(failure_report, manifest)
                if [cell(r) for r in failure_report.get("rows", [])] == [cell(r) for r in before.get("rows", [])]:
                    failures.append("rollback_test_did_not_demonstrate_mutation")
                if failure_report.get("parameters") != before.get("parameters") or failure_report.get("parameterSchema") != before.get("parameterSchema"):
                    failures.append("injected_failure_changed_model_or_schema")
        tests.append({"name": item.get("name"), "kind": item.get("kind"), "result": "passed" if not failures else "failed", "errors": sorted(set(failures)), "deltas": deltas})
    missing = sorted(REQUIRED_KINDS - {t["kind"] for t in tests if t["result"] == "passed"})
    complete = not errors and all(s["result"] == "passed" for s in structural) and not missing and all(t["result"] == "passed" for t in tests)
    return {"scope": "Native custom-group prototype, Revit 2025 disposable fixture only; no production or cross-version support claim",
            "completeRecordedPrototype": complete, "manifestErrors": errors,
            "structure": structural, "interactions": tests, "missingInteractionKinds": missing}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--baseline", help="Full baseline report filename; defaults to original-baseline.json if present")
    args = parser.parse_args()
    root = args.run.resolve()
    sources = []
    def read(name):
        path = (root / name).resolve()
        if not path.is_relative_to(root):
            raise ValueError("Evidence reference outside run directory")
        if not path.exists():
            return None
        data = path.read_bytes()
        sources.append({"file": str(path.relative_to(root)), "sha256": hashlib.sha256(data).hexdigest()})
        return json.loads(data.decode("utf-8-sig"))
    manifest = read("run.json")
    if manifest is None:
        raise SystemExit("run.json missing; no native qualification can be inferred")
    baseline_name = args.baseline or ("original-baseline.json" if (root / "original-baseline.json").exists() else "baseline.json")
    snapshots = {stage: read(baseline_name if stage == "baseline" else stage + ".json") for stage in STAGES}
    tests = read("interactions.json") or []
    for item in tests:
        item["beforeReport"] = read(item["before"])
        item["afterReport"] = read(item["after"])
        if item.get("failureSnapshot"):
            item["failureReport"] = read(item["failureSnapshot"])
    result = evaluate(manifest, snapshots, tests)
    result["sources"] = sources
    text = json.dumps(result, indent=2, ensure_ascii=False) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text, encoding="utf-8")
    print(text)


if __name__ == "__main__":
    main()
