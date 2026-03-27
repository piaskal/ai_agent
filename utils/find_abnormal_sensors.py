#!/usr/bin/env python3
"""Identify abnormal sensor JSON files in a ZIP archive."""

from __future__ import annotations

import argparse
import json
import re
import sys
import zipfile
from dataclasses import dataclass
from pathlib import Path


METRICS: dict[str, dict[str, str]] = {
    "temperature_K": {"label": "Temperature", "unit": "K", "token": "temperature", "min": 553.0, "max": 873.0},
    "pressure_bar": {"label": "Pressure", "unit": "bar", "token": "pressure", "min": 60.0, "max": 160.0},
    "water_level_meters": {"label": "Water Level", "unit": "m", "token": "water", "min": 5.0, "max": 15.0},
    "voltage_supply_v": {"label": "Supply Voltage", "unit": "V", "token": "voltage", "min": 229.0, "max": 231.0},
    "humidity_percent": {"label": "Humidity", "unit": "%", "token": "humidity", "min": 40.0, "max": 80.0},
}

NOTE_ALERT_PREFIXES: tuple[str, ...] = (
    "I am not comfortable",
    "I am seeing an unexpected pattern",
    "I can see a clear irregularity",
    "Something is clearly off",
    "The current result seems unreliable",
    "The latest behavior is concerning",
    "The numbers feel inconsistent",
    "The output quality is doubtful",
    "The situation requires attention",
    "These readings look suspicious",
    "This check did not look right",
    "This is not the pattern I expected",
    "This report raises serious doubts",
    "This run shows questionable behavior",
    "This state looks unstable",
)


@dataclass
class Reading:
    file_name: str
    timestamp: int
    sensor_type: str
    values: dict[str, float]
    raw: dict[str, object]


@dataclass
class Outlier:
    file_id: str
    timestamp: int
    sensor_type: str
    metric: str
    value: float
    reason: str
    expected_value: float | None = None
    allowed_min: float | None = None
    allowed_max: float | None = None
    matched_prefixes: list[str] | None = None


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Find abnormal sensor files using fixed value ranges for active metrics and zero checks for inactive metrics."
    )
    parser.add_argument("zip_file", nargs="?", default="sensors.zip", type=Path)
    parser.add_argument(
        "--output-json",
        type=Path,
        default=None,
        help="Optional file path for a JSON report.",
    )
    parser.add_argument(
        "--max-print",
        type=int,
        default=200,
        help="Maximum outlier rows to print in terminal (default: 200)",
    )
    parser.add_argument(
        "--inactive-zero-tol",
        type=float,
        default=0.0,
        help="Allowed absolute tolerance for inactive metrics expected to be zero (default: 0.0)",
    )
    return parser


def parse_args() -> tuple[argparse.ArgumentParser, argparse.Namespace]:
    parser = build_parser()
    return parser, parser.parse_args()


def fail_with_usage(parser: argparse.ArgumentParser, message: str) -> int:
    print(f"ERROR: {message}", file=sys.stderr)
    print(file=sys.stderr)
    parser.print_usage(sys.stderr)
    return 1


def metric_is_active(sensor_type: str, token: str) -> bool:
    return token in set(sensor_type.split("/"))


def to_file_id(file_name: str) -> str:
    return Path(file_name).stem


def find_note_prefixes(operator_notes: str) -> list[str]:
    note = operator_notes.strip().lower()
    matches: list[str] = []
    for prefix in NOTE_ALERT_PREFIXES:
        if note.startswith(prefix.lower()):
            matches.append(prefix)
    return matches


def load_readings(zip_path: Path) -> list[Reading]:
    if not zip_path.exists():
        raise FileNotFoundError(f"Zip file not found: {zip_path}")

    items: list[Reading] = []
    with zipfile.ZipFile(zip_path, "r") as archive:
        for file_name in archive.namelist():
            if not file_name.lower().endswith(".json"):
                continue
            with archive.open(file_name, "r") as handle:
                raw = json.loads(handle.read().decode("utf-8"))

            values: dict[str, float] = {}
            for metric in METRICS:
                values[metric] = float(raw.get(metric, 0))

            items.append(
                Reading(
                    file_name=file_name,
                    timestamp=int(raw["timestamp"]),
                    sensor_type=str(raw.get("sensor_type", "")),
                    values=values,
                    raw=raw,
                )
            )

    items.sort(key=lambda x: x.timestamp)
    return items


def detect_outliers(
    readings: list[Reading],
    inactive_zero_tol: float,
) -> list[Outlier]:
    results: list[Outlier] = []

    for row in readings:
        note_text = str(row.raw.get("operator_notes", ""))
        note_prefixes = find_note_prefixes(note_text)
        if note_prefixes:
            results.append(
                Outlier(
                    file_id=to_file_id(row.file_name),
                    timestamp=row.timestamp,
                    sensor_type=row.sensor_type,
                    metric="operator_notes",
                    value=0.0,
                    reason="operator_note_prefix",
                    matched_prefixes=note_prefixes,
                )
            )

        for metric, meta in METRICS.items():
            value = row.values[metric]
            is_active = metric_is_active(row.sensor_type, meta["token"])

            if not is_active:
                if abs(value) > inactive_zero_tol:
                    results.append(
                        Outlier(
                            file_id=to_file_id(row.file_name),
                            timestamp=row.timestamp,
                            sensor_type=row.sensor_type,
                            metric=metric,
                            value=value,
                            reason="inactive_nonzero",
                            expected_value=0.0,
                        )
                    )
                continue

            min_val = float(meta["min"])
            max_val = float(meta["max"])
            if value < min_val or value > max_val:
                results.append(
                    Outlier(
                        file_id=to_file_id(row.file_name),
                        timestamp=row.timestamp,
                        sensor_type=row.sensor_type,
                        metric=metric,
                        value=value,
                        reason="active_out_of_range",
                        allowed_min=min_val,
                        allowed_max=max_val,
                    )
                )

    def severity(item: Outlier) -> float:
        if item.reason == "inactive_nonzero":
            return float("inf")
        if item.reason == "operator_note_prefix":
            return 10_000.0
        if item.reason == "active_out_of_range":
            min_v = item.allowed_min or 0.0
            max_v = item.allowed_max or 0.0
            if item.value < min_v:
                return min_v - item.value
            if item.value > max_v:
                return item.value - max_v
        return 0.0

    results.sort(key=severity, reverse=True)
    return results


def to_json(outliers: list[Outlier]) -> list[dict[str, object]]:
    payload: list[dict[str, object]] = []
    for item in outliers:
        payload.append(
            {
                "file_id": item.file_id,
                "timestamp": item.timestamp,
                "sensor_type": item.sensor_type,
                "metric": item.metric,
                "value": item.value,
                "reason": item.reason,
                "expected_value": item.expected_value,
                "allowed_min": item.allowed_min,
                "allowed_max": item.allowed_max,
                "matched_prefixes": item.matched_prefixes,
            }
        )
    return payload


def main() -> int:
    parser, args = parse_args()

    if args.max_print < 0:
        return fail_with_usage(parser, "--max-print must be >= 0")
    if args.inactive_zero_tol < 0:
        return fail_with_usage(parser, "--inactive-zero-tol must be >= 0")

    try:
        readings = load_readings(args.zip_file)
    except (FileNotFoundError, OSError, json.JSONDecodeError, ValueError) as exc:
        return fail_with_usage(parser, str(exc))

    if not readings:
        print("ERROR: no JSON readings found in ZIP.", file=sys.stderr)
        return 1

    outliers = detect_outliers(
        readings,
        inactive_zero_tol=args.inactive_zero_tol,
    )
    abnormal_files = sorted({item.file_id for item in outliers})

    print(f"Total files: {len(readings)}")
    print(f"Outlier rows: {len(outliers)}")
    print(f"Files with at least one abnormal reading: {len(abnormal_files)}")
    print()

    if not outliers:
        print("No abnormal readings detected with current threshold.")
    else:
        limit = max(0, args.max_print)
        for item in outliers[:limit]:
            if item.reason == "operator_note_prefix":
                prefixes = " | ".join(item.matched_prefixes or [])
                print(
                    f"{item.file_id}\toperator_notes\treason=operator_note_prefix\t"
                    f"matched_prefixes={prefixes}\t{item.sensor_type}"
                )
                continue

            info = METRICS[item.metric]
            if item.reason == "inactive_nonzero":
                print(
                    f"{item.file_id}\t{item.metric}\t{item.value:.2f} {info['unit']}\t"
                    f"reason=inactive_nonzero\texpected=0.00\t{item.sensor_type}"
                )
                continue

            if item.reason == "active_out_of_range":
                print(
                    f"{item.file_id}\t{item.metric}\t{item.value:.2f} {info['unit']}\t"
                    f"reason=active_out_of_range\tallowed={item.allowed_min:.2f}..{item.allowed_max:.2f}\t"
                    f"{item.sensor_type}"
                )
                continue

            print(
                f"{item.file_id}\t{item.metric}\t{item.value:.2f} {info['unit']}\t"
                f"reason={item.reason}\t{item.sensor_type}"
            )
        if len(outliers) > limit:
            print(f"... truncated {len(outliers) - limit} rows (increase --max-print).")

    if args.output_json is not None:
        abnormal_file_set = set(abnormal_files)
        abnormal_files_content = {
            to_file_id(row.file_name): row.raw
            for row in readings
            if to_file_id(row.file_name) in abnormal_file_set
        }

        report = {
            "zip_file": str(args.zip_file),
            "inactive_zero_tol": args.inactive_zero_tol,
            "active_ranges": {
                metric: {
                    "min": float(meta["min"]),
                    "max": float(meta["max"]),
                }
                for metric, meta in METRICS.items()
            },
            "total_files": len(readings),
            "outlier_rows": len(outliers),
            "abnormal_files_count": len(abnormal_files),
            "abnormal_files": abnormal_files,
            "abnormal_files_content": abnormal_files_content,
            "outliers": to_json(outliers),
        }
        try:
            args.output_json.write_text(json.dumps(report, indent=2), encoding="utf-8")
        except OSError as exc:
            print(f"ERROR: cannot write JSON report: {exc}", file=sys.stderr)
            return 1
        print()
        print(f"JSON report written to: {args.output_json.resolve()}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
