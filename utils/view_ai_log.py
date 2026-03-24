#!/usr/bin/env python3
"""Generate an HTML report from AI verbose logs containing base64 images and JSON responses."""

from __future__ import annotations

import argparse
import html
import json
import re
import sys
import webbrowser
from dataclasses import dataclass
from pathlib import Path
from typing import Any

PAYLOAD_RE = re.compile(
    r"^(?P<timestamp>\d{4}-\d{2}-\d{2} [^\[]+) \[VRB\] Describe connection map image payload \(base64\): (?P<b64>[A-Za-z0-9+/=]+)$",
    re.MULTILINE,
)
INFO_RE = re.compile(
    r"^(?P<timestamp>\d{4}-\d{2}-\d{2} [^\[]+) \[INF\] Describing base64 image with model '(?P<model>[^']+)'(?: prompt '(?P<prompt>.*)')?\.$",
    re.MULTILINE,
)
RESPONSE_MARKER = "[VRB] Describe connection map model response:"


@dataclass
class ParsedEntry:
    timestamp: str
    image_base64: str
    model_name: str
    prompt_text: str | None
    description: str
    response_json: dict[str, Any] | None


def extract_description(response_json: dict[str, Any]) -> str:
    output = response_json.get("output", [])
    chunks: list[str] = []

    for item in output:
        if item.get("type") != "message":
            continue
        for content in item.get("content", []):
            if content.get("type") == "output_text":
                text = content.get("text")
                if isinstance(text, str) and text.strip():
                    chunks.append(text.strip())

    if chunks:
        return "\n\n".join(chunks)

    return "(No output_text description found in response JSON.)"


def parse_entries(log_text: str) -> list[ParsedEntry]:
    decoder = json.JSONDecoder()
    payload_matches = list(PAYLOAD_RE.finditer(log_text))
    info_matches = list(INFO_RE.finditer(log_text))
    info_index = 0
    latest_info: dict[str, str | None] = {}
    entries: list[ParsedEntry] = []

    for index, payload_match in enumerate(payload_matches):
        timestamp = payload_match.group("timestamp").strip()
        image_base64 = payload_match.group("b64").strip()
        while info_index < len(info_matches) and info_matches[info_index].start() < payload_match.start():
            match = info_matches[info_index]
            latest_info = {
                "model": match.group("model").strip(),
                "prompt": match.group("prompt").strip() if match.group("prompt") else None,
            }
            info_index += 1

        info = latest_info
        search_start = payload_match.end()
        search_end = payload_matches[index + 1].start() if index + 1 < len(payload_matches) else len(log_text)

        marker_pos = log_text.find(RESPONSE_MARKER, search_start, search_end)
        response_json: dict[str, Any] | None = None
        model_name = info.get("model") or "(Unknown model)"
        prompt_text = info.get("prompt")
        description = "(No model response block found after this image payload.)"

        if marker_pos != -1:
            json_start = log_text.find("{", marker_pos, search_end)
            if json_start != -1:
                try:
                    parsed_obj, _ = decoder.raw_decode(log_text[json_start:search_end])
                    if isinstance(parsed_obj, dict):
                        response_json = parsed_obj
                        raw_model = parsed_obj.get("model")
                        if isinstance(raw_model, str) and raw_model.strip():
                            model_name = raw_model.strip()
                        description = extract_description(parsed_obj)
                    else:
                        description = "(Model response JSON was parsed, but it is not an object.)"
                except json.JSONDecodeError as exc:
                    description = f"(Failed to parse response JSON: {exc})"

        entries.append(
            ParsedEntry(
                timestamp=timestamp,
                image_base64=image_base64,
                model_name=model_name,
                prompt_text=prompt_text,
                description=description,
                response_json=response_json,
            )
        )

    return list(reversed(entries))


def render_html(entries: list[ParsedEntry], source_file: Path) -> str:
    cards: list[str] = []

    for i, entry in enumerate(entries, 1):
        escaped_description = html.escape(entry.description)
        escaped_timestamp = html.escape(entry.timestamp)
        escaped_model_name = html.escape(entry.model_name)
        prompt_html = ""
        raw_json_html = ""

        if entry.prompt_text:
            prompt_html = (
                "<h3>Prompt</h3>"
                f"<pre class=\"prompt\">{html.escape(entry.prompt_text)}</pre>"
            )

        if entry.response_json is not None:
            pretty_json = json.dumps(entry.response_json, indent=2, ensure_ascii=True)
            raw_json_html = (
                "<details><summary>Show raw response JSON</summary>"
                f"<pre class=\"json\">{html.escape(pretty_json)}</pre></details>"
            )

        cards.append(
            f"""
<section class="card">
  <h2>Entry {i}</h2>
  <p class="meta">{escaped_timestamp}</p>
  <div class="image-wrap">
    <img src="data:image/png;base64,{entry.image_base64}" alt="Log image {i}" />
  </div>
    {prompt_html}
  <h3>Description</h3>
    <p class="model">Model: {escaped_model_name}</p>
  <pre class="description">{escaped_description}</pre>
  {raw_json_html}
</section>
""".strip()
        )

    card_html = "\n\n".join(cards) if cards else "<p>No image payload entries were found.</p>"

    return f"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>AI Log Viewer</title>
  <style>
    :root {{
      --bg: #f7f4ef;
      --panel: #ffffff;
      --text: #27251f;
      --muted: #6b6458;
      --accent: #0a6e61;
      --border: #ded6c8;
    }}
    * {{ box-sizing: border-box; }}
    body {{
      margin: 0;
      font-family: "Segoe UI", Tahoma, sans-serif;
      color: var(--text);
      background: radial-gradient(circle at 20% 0%, #efe8da 0%, var(--bg) 50%);
      line-height: 1.45;
    }}
    .container {{
      max-width: 1200px;
      margin: 0 auto;
      padding: 24px;
    }}
    h1 {{ margin-top: 0; }}
    .subtitle {{ color: var(--muted); margin-top: -8px; }}
    .card {{
      background: var(--panel);
      border: 1px solid var(--border);
      border-radius: 14px;
      padding: 18px;
      margin-bottom: 18px;
      box-shadow: 0 6px 24px rgba(0, 0, 0, 0.06);
    }}
    .meta {{ color: var(--muted); margin-top: -4px; }}
        .model {{
            margin: 0 0 8px;
            font-weight: 600;
            color: var(--accent);
        }}
    .image-wrap {{
      border: 1px solid var(--border);
      border-radius: 10px;
      background: #fbfaf8;
      padding: 10px;
      overflow: auto;
            text-align: center;
    }}
    img {{
      display: block;
            width: 50%;
            max-width: 100%;
      height: auto;
            margin: 0 auto;
    }}
    .description {{
      white-space: pre-wrap;
      background: #faf8f3;
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 12px;
      font-family: Consolas, "Courier New", monospace;
      font-size: 14px;
      overflow-x: auto;
    }}
        .prompt {{
            white-space: pre-wrap;
            background: #f3f7f5;
            border: 1px solid var(--border);
            border-radius: 8px;
            padding: 12px;
            font-family: Consolas, "Courier New", monospace;
            font-size: 14px;
            overflow-x: auto;
        }}
    details {{ margin-top: 10px; }}
    summary {{ cursor: pointer; color: var(--accent); font-weight: 600; }}
    .json {{
      white-space: pre-wrap;
      overflow-x: auto;
      background: #f5f3ee;
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 10px;
      font-family: Consolas, "Courier New", monospace;
      font-size: 13px;
    }}
  </style>
</head>
<body>
  <main class="container">
    <h1>AI Log Viewer</h1>
    <p class="subtitle">Source: {html.escape(str(source_file))} | Entries found: {len(entries)}</p>
    {card_html}
  </main>
</body>
</html>
"""


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Extract base64 images and response descriptions from a verbose log and build an HTML report."
    )
    parser.add_argument("log_file", type=Path, help="Path to the verbose log file")
    parser.add_argument(
        "-o",
        "--output",
        type=Path,
        default=Path("ai-log-viewer.html"),
        help="Output HTML file path (default: ai-log-viewer.html)",
    )
    parser.add_argument(
        "--open",
        action="store_true",
        help="Open the generated report in the default browser",
    )
    return parser


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()

    if not args.log_file.exists():
        print(f"ERROR: Log file not found: {args.log_file}", file=sys.stderr)
        return 1

    try:
        log_text = args.log_file.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        print(f"ERROR: Could not read log file: {exc}", file=sys.stderr)
        return 1

    entries = parse_entries(log_text)
    report_html = render_html(entries, args.log_file)

    try:
        args.output.write_text(report_html, encoding="utf-8")
    except OSError as exc:
        print(f"ERROR: Could not write output file: {exc}", file=sys.stderr)
        return 1

    print(f"Generated report: {args.output.resolve()}")
    print(f"Entries found: {len(entries)}")

    if args.open:
        webbrowser.open(args.output.resolve().as_uri())

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
