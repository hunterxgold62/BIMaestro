"""Summarize compact native perf packets without touching Revit or raw evidence.

Example: python CollectPerf.py native.json custom.json --baseline native.json
"""
import argparse
import hashlib
import json
import math
from pathlib import Path


def number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def summarize(path):
    raw = path.read_bytes()
    packet = json.loads(raw.decode("utf-8-sig"))
    warnings = []
    if any(k in packet for k in ("parameters", "parameterSchema", "modelSnapshot")):
        warnings.append("contains_full_model_capture_not_compact_perf")
    if len(raw) > 65536:
        warnings.append("payload_exceeds_64KiB_review_measurement_overhead")
    mode = packet.get("auditMode", packet.get("mode"))
    if mode not in ("fast", "interactive"):
        warnings.append("mode_not_fast_or_interactive")
    diagnostics = packet.get("diagnosticTimerEnabled", packet.get("diagnostics"))
    required = ("pid", "version", "build", "scenario", "fixtureId")
    warnings += ["missing_context_" + key for key in required if key not in packet]
    if diagnostics is None:
        warnings.append("missing_context_diagnosticTimerEnabled")
    if diagnostics is True:
        warnings.append("diagnostic_timer_active_measure_its_overhead_separately")
    timings = packet.get("metrics", {}).get("Timings", {})
    verified = {}
    for name, value in timings.items():
        count = value.get("Count")
        fields = ("MeanMs", "P95Ms", "MaxMs")
        valid = isinstance(count, int) and not isinstance(count, bool) and count > 0
        valid = valid and all(number(value.get(k)) and value[k] >= 0 for k in fields)
        valid = valid and value.get("MaxMs", -1) + 1e-6 >= max(value.get("MeanMs", 0), value.get("P95Ms", 0))
        if not valid:
            warnings.append("invalid_or_empty_metric_" + name)
            continue
        verified[name] = {key: value[key] for key in ("Count", *fields)}
    if not verified:
        warnings.append("no_valid_timing_samples")
    group = packet.get("group") or {}
    stable = group.get("headerNative") is True and group.get("memberCount") == 6 and group.get("mappingMatches") is True
    if not group:
        warnings.append("native_group_verification_not_in_perf_packet")
    if packet.get("customGroupExpected") is True and not stable:
        warnings.append("custom_group_not_verified_at_window_end")
    counters = packet.get("counters", {})
    if counters.get("failures", 0) or counters.get("automaticStops", 0):
        warnings.append("failures_or_automatic_stops_recorded")
    result = {"file": str(path), "sha256": hashlib.sha256(raw).hexdigest(), "bytes": len(raw),
              "pid": packet.get("pid"), "version": packet.get("version"), "build": packet.get("build"),
              "auditMode": mode, "scenario": packet.get("scenario"), "fixtureId": packet.get("fixtureId"),
              "diagnosticTimerEnabled": diagnostics,
              "reportedModeField": "auditMode" if "auditMode" in packet else "mode",
              "reportedDiagnosticsField": "diagnosticTimerEnabled" if "diagnosticTimerEnabled" in packet else "diagnostics",
              "autoGroup": packet.get("autoGroup"), "modelRevision": packet.get("modelRevision"),
              "reportedState": packet.get("state"),
              "verifiedNativeGroupAtEnd": stable, "counters": counters,
              "timings": verified, "warnings": warnings,
              "aggregationLimit": "Reported per-window aggregates; P95 is not recomputed without raw samples and is never averaged across windows."}
    latency = packet.get("observedLatencyMs")
    if latency is not None:
        if isinstance(latency, list) and latency and all(number(v) and v >= 0 for v in latency):
            samples = sorted(latency)
            result["observedEndToEndLatencyMs"] = {"count": len(samples), "median": samples[len(samples) // 2] if len(samples) % 2 else (samples[len(samples)//2-1] + samples[len(samples)//2])/2,
                                                   "p95": samples[math.ceil(.95 * len(samples)) - 1], "max": samples[-1]}
        else:
            warnings.append("invalid_observed_latency_samples")
    return result


def compare(baseline, candidate):
    limits = []
    for key in ("version", "build", "scenario", "fixtureId", "diagnosticTimerEnabled"):
        if baseline.get(key) is None or candidate.get(key) is None:
            limits.append("missing_comparison_context_" + key)
        elif baseline[key] != candidate[key]:
            limits.append("different_" + key)
    if baseline.get("auditMode") not in ("fast", "interactive") or candidate.get("auditMode") not in ("fast", "interactive"):
        limits.append("both_windows_must_use_fast_or_interactive_mode")
    if not candidate.get("verifiedNativeGroupAtEnd"):
        limits.append("candidate_does_not_prove_native_group_retained")
    if baseline.get("warnings") or candidate.get("warnings"):
        limits.append("packet_warnings_require_review")
    changes = {}
    for name in baseline["timings"].keys() & candidate["timings"].keys():
        left, right = baseline["timings"][name], candidate["timings"][name]
        changes[name] = {"baselineCount": left["Count"], "candidateCount": right["Count"],
                         "meanMsDelta": round(right["MeanMs"] - left["MeanMs"], 6),
                         "p95MsDelta": round(right["P95Ms"] - left["P95Ms"], 6),
                         "maxMsDelta": round(right["MaxMs"] - left["MaxMs"], 6)}
    if not changes:
        limits.append("no_shared_timing_metric")
    return {"baseline": baseline["file"], "candidate": candidate["file"],
            "comparisonContextComplete": not limits, "limits": limits, "metricDeltas": changes,
            "interpretation": "Arithmetic comparison only; automation end-to-end latency and native method durations are separate measurements. No functional qualification is inferred."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("files", nargs="+", type=Path)
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    windows = [summarize(p) for p in args.files]
    result = {"scope": "Perf packet audit only; no native functionality or release support claim", "windows": windows}
    if args.baseline:
        baseline = summarize(args.baseline)
        result["comparisons"] = [compare(baseline, window) for window in windows if Path(window["file"]).resolve() != args.baseline.resolve()]
    text = json.dumps(result, indent=2, ensure_ascii=False) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text, encoding="utf-8")
    print(text)


if __name__ == "__main__":
    main()
