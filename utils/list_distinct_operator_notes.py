#!/usr/bin/env python3
"""List all distinct operator_notes values from sensor JSON files in a ZIP archive."""

from __future__ import annotations

import argparse
import json
import sys
import zipfile
from pathlib import Path


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Return all distinct operator_notes values from zipped sensor JSON files."
    )
    parser.add_argument("zip_file", nargs="?", default="sensors.zip", type=Path)
    parser.add_argument(
        "--output-json",
        type=Path,
        default=None,
        help="Optional output path for a JSON array of notes.",
    )
    return parser


def fail_with_usage(parser: argparse.ArgumentParser, message: str) -> int:
    print(f"ERROR: {message}", file=sys.stderr)
    print(file=sys.stderr)
    parser.print_usage(sys.stderr)
    return 1


def collect_distinct_notes(zip_path: Path) -> list[str]:
    if not zip_path.exists():
        raise FileNotFoundError(f"Zip file not found: {zip_path}")

    notes: set[str] = set()
    with zipfile.ZipFile(zip_path, "r") as archive:
        for file_name in archive.namelist():
            if not file_name.lower().endswith(".json"):
                continue
            with archive.open(file_name, "r") as handle:
                raw = json.loads(handle.read().decode("utf-8"))
            note = raw.get("operator_notes")
            if isinstance(note, str) and note.strip():
                notes.add(note.strip())

    return sorted(notes)


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()

    try:
        notes = collect_distinct_notes(args.zip_file)
    except (FileNotFoundError, OSError, json.JSONDecodeError, ValueError) as exc:
        return fail_with_usage(parser, str(exc))

    print(f"Distinct operator notes: {len(notes)}")
    print()
    for note in notes:
        print(note)

    if args.output_json is not None:
        try:
            args.output_json.write_text(json.dumps(notes, indent=2), encoding="utf-8")
        except OSError as exc:
            print(f"ERROR: cannot write JSON output: {exc}", file=sys.stderr)
            return 1
        print()
        print(f"JSON written to: {args.output_json.resolve()}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
