"""Synthetic evidence tests. They test the checker, never native Revit behavior."""
from copy import deepcopy
import unittest
from CollectResults import evaluate


def row(label, value="", marker="", ident=None, storage=None):
    return {"label": label, "value": value, "marker": marker, "editable": not marker,
            "isNativeGroupHeader": bool(marker), "rect": {"top": 0, "bottom": 24 if label else 0},
            "_id": ident, "_storage": storage}


def report(rows, second=0):
    result = {"version": "2025", "build": "25.test", "pid": 99, "path": "CustomGroupFixture.rvt",
              "time": f"2026-10-23T10:00:{second:02d}Z", "rows": [], "mappings": [], "parameters": {}, "parameterSchema": {}}
    for index, item in enumerate(deepcopy(rows), 1):
        ident, storage = item.pop("_id"), item.pop("_storage")
        item["row"] = index
        result["rows"].append(item)
        if ident is not None:
            key = f"wall|{ident}|{item['label']}"
            result["parameters"][key] = item["value"]
            result["parameterSchema"][key] = {"storage": storage, "readOnly": False, "groupTypeId": "source-A" if ident in (1, 9) else "source-B"}
            result["mappings"].append({"id": ident, "nativeRow": index, "matches": True, "expectedLabel": item["label"], "actualLabel": item["label"]})
    return result


def fixture():
    header_a, header_b = row("A", marker="^"), row("B", marker="^")
    text = row("Text", "base", ident=1, storage="String")
    boolean = row("Boolean", "0", ident=2, storage="Integer")
    number = row("Number", "0", ident=3, storage="Double")
    level = row("Level", "11", ident=4, storage="ElementId")
    neighbor = row("Neighbor", "outside", ident=9, storage="String")
    filler, spacer = row(""), row("")
    original = [header_a, text, neighbor, header_b, boolean, number, level, filler]
    custom = row("Custom", marker="^")
    grouped = [custom, text, boolean, number, level, spacer, header_a, neighbor, header_b, filler]
    baseline = report(original)
    snapshots = {"baseline": baseline,
                 "inserted": report([custom, spacer] + original, 1),
                 "grouped": report(grouped, 2), "collapsed": report(grouped, 3),
                 "expanded": report(grouped, 4), "restored": report(original, 5)}
    snapshots["collapsed"]["rows"][0]["marker"] = "v"
    for mapping in snapshots["collapsed"]["mappings"]:
        if mapping["id"] in (1, 2, 3, 4):
            snapshots["collapsed"]["rows"][mapping["nativeRow"] - 1]["rect"]["bottom"] = 0
    manifest = {"version": "2025", "pid": 99, "fixturePath": "CustomGroupFixture.rvt", "headerLabel": "Custom",
                "memberParameterIds": [1, 2, 3, 4], "insertedRowCount": 2,
                "nativeInsertionVerified": True, "operationExports": ["?addGroupHeader@ElemGrid@@EXACT_TEST_SIGNATURE"],
                "moduleHashes": {"DesktopMFC.dll": "a" * 64}}
    tests = []
    for kind, ident, value in (("text", 1, "written"), ("boolean", 2, "1"), ("double", 3, "1.25"), ("elementId", 4, "22"), ("neighbor", 9, "elsewhere")):
        before, after = deepcopy(snapshots["grouped"]), deepcopy(snapshots["grouped"])
        before["time"], after["time"] = "2026-10-23T10:00:06Z", "2026-10-23T10:00:07Z"
        key = next(k for k in after["parameters"] if k.split("|")[1] == str(ident))
        after["parameters"][key] = value
        tests.append({"name": kind, "kind": kind, "targetKey": key, "expectedChanges": {key: value}, "beforeReport": before, "afterReport": after})
    for kind in ("cancel", "rollback", "active-editor-refusal"):
        before, after = deepcopy(baseline), deepcopy(baseline)
        after["time"] = "2026-10-23T10:00:07Z"
        item = {"name": kind, "kind": kind, "expectedChanges": {}, "beforeReport": before, "afterReport": after}
        if kind == "rollback":
            item["failureReport"] = deepcopy(snapshots["inserted"])
        if kind == "active-editor-refusal":
            before["nativeState"] = {"activeCell": 1, "currentRow": 2, "currentColumn": 2,
                                     "children": [{"className": "GXEDIT", "text": "pending buffer", "visible": True}]}
            after["nativeState"] = deepcopy(before["nativeState"])
        tests.append(item)
    for kind, previous, following in (("undo", "written", "base"), ("redo", "base", "written")):
        before, after = deepcopy(baseline), deepcopy(baseline)
        key = "wall|1|Text"
        before["parameters"][key], after["parameters"][key] = previous, following
        after["time"] = "2026-10-23T10:00:07Z"
        tests.append({"name": kind, "kind": kind, "expectedChanges": {key: following}, "beforeReport": before, "afterReport": after})
    return manifest, snapshots, tests


class CheckerTests(unittest.TestCase):
    def setUp(self):
        self.manifest, self.snapshots, self.interactions = fixture()

    def result(self):
        return evaluate(self.manifest, self.snapshots, self.interactions)

    def test_complete_synthetic_record_passes_checker_only(self):
        self.assertTrue(self.result()["completeRecordedPrototype"])

    def test_explicit_two_row_header_and_spacer_accepted(self):
        self.assertTrue(all(s["result"] == "passed" for s in self.result()["structure"]))

    def test_autodeclared_bool_without_native_header_test_rejected(self):
        self.snapshots["grouped"]["rows"][0]["isNativeGroupHeader"] = False
        self.assertFalse(self.result()["completeRecordedPrototype"])

    def test_header_still_editable_rejected(self):
        self.snapshots["grouped"]["rows"][0]["editable"] = True
        self.assertFalse(self.result()["completeRecordedPrototype"])

    def test_duplicate_parameter_row_rejected(self):
        self.snapshots["grouped"]["mappings"][1]["nativeRow"] = 2
        self.assertFalse(self.result()["completeRecordedPrototype"])

    def test_parameter_definition_group_change_rejected(self):
        self.snapshots["grouped"]["parameterSchema"]["wall|1|Text"]["groupTypeId"] = "Custom"
        self.assertFalse(self.result()["completeRecordedPrototype"])

    def test_collapse_hiding_neighbor_rejected(self):
        self.snapshots["collapsed"]["rows"][7]["rect"]["bottom"] = 0
        self.assertFalse(self.result()["completeRecordedPrototype"])

    def test_collateral_write_rejected(self):
        self.interactions[0]["afterReport"]["parameters"]["wall|9|Neighbor"] = "corrupted"
        self.assertFalse(self.result()["completeRecordedPrototype"])

    def test_same_string_editor_relabelled_double_rejected(self):
        self.interactions[0]["kind"] = "double"
        self.assertFalse(self.result()["completeRecordedPrototype"])

    def test_wrong_rollback_structure_rejected(self):
        rollback = next(t for t in self.interactions if t["kind"] == "rollback")
        rollback["afterReport"] = deepcopy(self.snapshots["grouped"])
        self.assertFalse(self.result()["completeRecordedPrototype"])

    def test_unchanged_value_is_not_proof_of_undo(self):
        undo = next(t for t in self.interactions if t["kind"] == "undo")
        undo["afterReport"] = deepcopy(undo["beforeReport"])
        undo["expectedChanges"] = {}
        self.assertFalse(self.result()["completeRecordedPrototype"])

    def test_missing_schema_or_interaction_is_not_qualified(self):
        del self.snapshots["baseline"]["parameterSchema"]
        self.interactions.pop()
        self.assertFalse(self.result()["completeRecordedPrototype"])

    def test_refusal_without_actual_active_editor_is_not_demonstrated(self):
        item = next(t for t in self.interactions if t["kind"] == "active-editor-refusal")
        item["beforeReport"]["nativeState"]["activeCell"] = 0
        self.assertFalse(self.result()["completeRecordedPrototype"])

    def test_editor_buffer_discarded_by_refusal_rejected(self):
        item = next(t for t in self.interactions if t["kind"] == "active-editor-refusal")
        item["afterReport"]["nativeState"]["children"][0]["text"] = "base"
        self.assertFalse(self.result()["completeRecordedPrototype"])

    def test_noop_is_not_rollback_proof(self):
        item = next(t for t in self.interactions if t["kind"] == "rollback")
        item["failureReport"] = deepcopy(item["beforeReport"])
        self.assertFalse(self.result()["completeRecordedPrototype"])


if __name__ == "__main__":
    unittest.main()
