"""Checks that the evidence collector rejects misleading or incomplete records."""
import json
import tempfile
import unittest
from pathlib import Path
from CollectResults import SCENARIOS, scenario


class EvidenceChecks(unittest.TestCase):
    def setUp(self):
        self.workspace_temp = Path(__file__).resolve().parents[2] / "tmp/properties-order-tests/collector-tests"
        self.workspace_temp.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=self.workspace_temp)
        self.root = Path(self.temp.name)
        self.run = self.root / "2025-fixture"
        self.run.mkdir()
        self.before = {"pid": 7, "version": "2025", "build": "test", "path": "fixture.rvt",
                       "time": "2026-10-21T10:00:00Z", "hasApplied": True,
                       "parameters": {"wall|-1010106|Comments": "ORDER_BASELINE", "wall|9|Other": "unchanged"},
                       "rows": [{"row": 1, "label": "", "marker": ""},
                                {"row": 2, "label": "Identity", "marker": "^"},
                                {"row": 3, "label": "Comments", "marker": ""}],
                       "mappings": [{"id": -1010106, "nativeRow": 3, "matches": True}]}
        self.after = json.loads(json.dumps(self.before))
        self.after.update(time="2026-10-21T10:00:01Z", hasApplied=False)
        self.after["parameters"]["wall|-1010106|Comments"] = "ORDER_BASELINEx"

    def tearDown(self):
        if not self.root.resolve().is_relative_to(self.workspace_temp.resolve()):
            raise RuntimeError("Refusing cleanup outside collector test workspace")
        self.temp.cleanup()

    def collect(self):
        for name, report in (("before", self.before), ("after", self.after)):
            (self.run / f"text-{name}.json").write_text(json.dumps(report), encoding="utf-8")
        return scenario(self.run, "text", SCENARIOS["text"], self.root)

    def test_later_deselection_does_not_invalidate_proven_order(self):
        self.assertEqual("passed_recorded_scenario", self.collect()["result"])

    def test_successful_write_without_active_order_is_not_qualification(self):
        self.before["hasApplied"] = False
        self.assertIn("active_target_order_not_demonstrated_before_input", self.collect()["failures"])

    def test_unrelated_parameter_change_fails(self):
        self.after["parameters"]["wall|9|Other"] = "unexpected"
        self.assertIn("unexpected_parameter_changes", self.collect()["failures"])

    def test_visible_label_without_native_identity_is_insufficient(self):
        self.before["mappings"][0]["matches"] = False
        self.assertIn("native_mapping_mismatch_before_input", self.collect()["failures"])

    def test_mixed_process_records_fail(self):
        self.after["pid"] = 8
        self.assertIn("inconsistent_pid", self.collect()["failures"])

    def test_wrong_value_fails(self):
        self.after["parameters"]["wall|-1010106|Comments"] = "WRONG"
        self.assertIn("target_value_not_expected", self.collect()["failures"])


if __name__ == "__main__":
    unittest.main()
