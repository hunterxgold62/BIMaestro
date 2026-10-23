"""Read-only summary of native Revit benchmark evidence; never substitutes for UI tests."""
import argparse
import json
import math
from pathlib import Path
from statistics import median


def summarize(report):
    samples = report.get("samples", [])
    times = sorted(s["readyMs"] for s in samples)
    errors = []
    if report.get("pass") is not True or report.get("failures"):
        errors.append("benchmark_failed")
    if len(samples) != 30 or {s["step"] for s in samples} != set(range(30)):
        errors.append("incomplete_transition_sequence")
    if any(sum(s["target"] == target for s in samples) != 5 for target in range(6)):
        errors.append("unbalanced_targets")
    return {
        "errors": errors,
        "count": len(times),
        "medianMs": median(times) if times else None,
        "p95Ms": times[math.ceil(len(times) * .95) - 1] if times else None,
        "maxMs": max(times) if times else None,
        "nativeRebuilds": report.get("nativeRebuilds"),
        "metrics": report.get("metrics"),
        "limitations": report.get("limitations"),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("run", type=Path)
    args = parser.parse_args()
    read = lambda name: json.loads((args.run / name).read_text(encoding="utf-8-sig"))
    output = {"run": str(args.run.resolve()), "errors": []}
    for mode in ("native", "combined"):
        name = "result-bench-" + mode + ".json"
        if not (args.run / name).exists():
            output[mode] = {"errors": ["not_recorded"]}
        else:
            output[mode] = summarize(read(name))
        output["errors"].extend(mode + ":" + e for e in output[mode]["errors"])
    before_file = args.run / "report-compact-audit-before.json"
    after_file = args.run / "report-compact-audit-after.json"
    if before_file.exists() and after_file.exists():
        before, after = read(before_file.name)["audit"], read(after_file.name)["audit"]
        output["integrity"] = {"unchanged": before == after, "before": before, "after": after}
        if before != after:
            output["errors"].append("model_values_or_schema_changed")
    else:
        output["errors"].append("integrity_not_recorded")
    output["pass"] = not output["errors"]
    (args.run / "combined-evaluation.json").write_text(json.dumps(output, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in output.items() if k not in ("native", "combined")}, ensure_ascii=False, indent=2))
    for mode in ("native", "combined"):
        print(mode, json.dumps({k: v for k, v in output[mode].items() if k != "metrics"}, ensure_ascii=False))
    raise SystemExit(0 if output["pass"] else 1)


if __name__ == "__main__":
    main()
