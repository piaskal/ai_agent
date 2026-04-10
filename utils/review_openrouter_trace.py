#!/usr/bin/env python3
"""Build an HTML report for reviewing OpenRouter trace logs."""

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

SCRIPT_DIR = Path(__file__).resolve().parent
DEFAULT_LOGS_DIR = SCRIPT_DIR.parent / "OpenRouterAgent.Console" / "logs"

TIMESTAMP_LINE_RE = re.compile(
    r"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2} \[[A-Z]{3}\] ",
    re.MULTILINE,
)
REQUEST_MARKER_RE = re.compile(
    r"^(?P<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[VRB\] OpenRouter request:\s*$",
    re.MULTILINE,
)
RESPONSE_MARKER_RE = re.compile(
    r"^(?P<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[VRB\] OpenRouter response:\s*$",
    re.MULTILINE,
)
EXECUTING_TOOL_RE = re.compile(
    r"^(?P<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[INF\] Executing tool call '(?P<name>[^']+)' \(id: (?P<call_id>[^)]+)\) with arguments: (?P<arguments>.*)$",
    re.MULTILINE,
)
TOOL_FAILURE_RE = re.compile(
    r"^(?P<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[WRN\] Tool execution failed for '(?P<name>[^']+)'.\s*$",
    re.MULTILINE,
)


@dataclass
class JsonBlock:
    timestamp: str
    start: int
    end: int
    payload: dict[str, Any]


@dataclass
class ToolExecutionEvent:
    timestamp: str
    call_id: str
    name: str
    arguments_text: str
    failure_text: str | None = None


@dataclass
class Turn:
    index: int
    request: JsonBlock
    response: JsonBlock | None


def parse_json_blocks(log_text: str, marker_re: re.Pattern[str]) -> list[JsonBlock]:
    decoder = json.JSONDecoder()
    blocks: list[JsonBlock] = []

    for marker in marker_re.finditer(log_text):
        json_start = log_text.find("{", marker.end())
        if json_start == -1:
            continue

        try:
            parsed, consumed = decoder.raw_decode(log_text[json_start:])
        except json.JSONDecodeError:
            continue

        if not isinstance(parsed, dict):
            continue

        blocks.append(
            JsonBlock(
                timestamp=marker.group("timestamp"),
                start=marker.start(),
                end=json_start + consumed,
                payload=parsed,
            )
        )

    return blocks


def parse_tool_executions(log_text: str) -> dict[str, ToolExecutionEvent]:
    events: dict[str, ToolExecutionEvent] = {}
    ordered_call_ids: list[str] = []

    for match in EXECUTING_TOOL_RE.finditer(log_text):
        event = ToolExecutionEvent(
            timestamp=match.group("timestamp"),
            call_id=match.group("call_id"),
            name=match.group("name"),
            arguments_text=match.group("arguments").strip(),
        )
        events[event.call_id] = event
        ordered_call_ids.append(event.call_id)

    unresolved = [events[call_id] for call_id in ordered_call_ids]
    for match in TOOL_FAILURE_RE.finditer(log_text):
        next_marker = TIMESTAMP_LINE_RE.search(log_text, match.end())
        failure_end = next_marker.start() if next_marker else len(log_text)
        failure_text = log_text[match.start():failure_end].strip()

        for event in unresolved:
            if event.name == match.group("name") and event.failure_text is None:
                event.failure_text = failure_text
                break

    return events


def build_turns(requests: list[JsonBlock], responses: list[JsonBlock]) -> list[Turn]:
    turns: list[Turn] = []
    response_index = 0

    for index, request in enumerate(requests, 1):
        response: JsonBlock | None = None
        while response_index < len(responses):
            candidate = responses[response_index]
            response_index += 1
            if candidate.start > request.end:
                response = candidate
                break

        turns.append(Turn(index=index, request=request, response=response))

    return turns


def extract_role_messages(request_payload: dict[str, Any], role: str) -> list[str]:
    results: list[str] = []
    for item in request_payload.get("input", []):
        if not isinstance(item, dict) or item.get("role") != role:
            continue
        content = item.get("content")
        if isinstance(content, str) and content.strip():
            results.append(content)
    return results


def extract_tool_outputs(request_payload: dict[str, Any]) -> list[dict[str, str]]:
    outputs: list[dict[str, str]] = []
    for item in request_payload.get("input", []):
        if not isinstance(item, dict) or item.get("type") != "function_call_output":
            continue
        outputs.append(
            {
                "call_id": stringify(item.get("call_id")),
                "output": pretty_text(item.get("output")),
            }
        )
    return outputs


def extract_response_tool_calls(response_payload: dict[str, Any]) -> list[dict[str, str]]:
    calls: list[dict[str, str]] = []
    for item in response_payload.get("output", []):
        if not isinstance(item, dict) or item.get("type") != "function_call":
            continue
        calls.append(
            {
                "call_id": stringify(item.get("call_id")),
                "name": stringify(item.get("name")),
                "arguments": pretty_text(item.get("arguments")),
            }
        )
    return calls


def extract_response_messages(response_payload: dict[str, Any]) -> list[str]:
    messages: list[str] = []
    for item in response_payload.get("output", []):
        if not isinstance(item, dict) or item.get("type") != "message":
            continue
        for content in item.get("content", []):
            if not isinstance(content, dict):
                continue
            if content.get("type") == "output_text":
                text = content.get("text")
                if isinstance(text, str) and text.strip():
                    messages.append(text)
    return messages


def extract_reasoning(response_payload: dict[str, Any]) -> str | None:
    chunks: list[str] = []

    reasoning_meta = response_payload.get("reasoning")
    if isinstance(reasoning_meta, dict):
        effort = reasoning_meta.get("effort")
        summary = reasoning_meta.get("summary")
        if effort:
            chunks.append(f"effort: {effort}")
        if isinstance(summary, str) and summary.strip():
            chunks.append(summary.strip())

    for item in response_payload.get("output", []):
        if not isinstance(item, dict) or item.get("type") != "reasoning":
            continue
        summary = item.get("summary")
        if isinstance(summary, list) and summary:
            for part in summary:
                text = None
                if isinstance(part, str):
                    text = part
                elif isinstance(part, dict):
                    text = stringify(part.get("text"))
                if text and text.strip():
                    chunks.append(text.strip())

    usage = response_payload.get("usage")
    if isinstance(usage, dict):
        output_tokens = usage.get("output_tokens_details")
        if isinstance(output_tokens, dict):
            reasoning_tokens = output_tokens.get("reasoning_tokens")
            if reasoning_tokens is not None:
                chunks.append(f"reasoning_tokens: {reasoning_tokens}")

    if not chunks:
        return None

    return "\n".join(chunks)


def stringify(value: Any) -> str:
    if value is None:
        return ""
    if isinstance(value, str):
        return value
    return json.dumps(value, indent=2, ensure_ascii=False)


def pretty_text(value: Any) -> str:
    text = stringify(value)
    stripped = text.strip()
    if not stripped:
        return ""

    try:
        parsed = json.loads(stripped)
    except json.JSONDecodeError:
        return text

    return json.dumps(parsed, indent=2, ensure_ascii=False)


def html_block(text: str) -> str:
    return f'<pre>{html.escape(text)}</pre>'


def render_section(title: str, blocks: list[str]) -> str:
    if not blocks:
        return ""
    inner = "\n".join(blocks)
    return f"<section><h3>{html.escape(title)}</h3>{inner}</section>"


def render_turn(turn: Turn, tool_events: dict[str, ToolExecutionEvent]) -> str:
    request_payload = turn.request.payload
    response_payload = turn.response.payload if turn.response is not None else {}
    system_prompts = extract_role_messages(request_payload, "system")
    user_prompts = extract_role_messages(request_payload, "user")
    tool_outputs = extract_tool_outputs(request_payload)
    tool_calls = extract_response_tool_calls(response_payload)
    llm_messages = extract_response_messages(response_payload)
    reasoning = extract_reasoning(response_payload)
    model = stringify(response_payload.get("model") or request_payload.get("model") or "")

    system_blocks = [html_block(prompt) for prompt in system_prompts]
    user_blocks = [html_block(prompt) for prompt in user_prompts]

    tool_output_blocks: list[str] = []
    for output in tool_outputs:
        call_id = output["call_id"]
        header = f"<p class=\"meta\">call_id: {html.escape(call_id)}</p>" if call_id else ""
        runtime = tool_events.get(call_id)
        failure_html = ""
        if runtime and runtime.failure_text:
            failure_html = (
                "<details><summary>Runtime failure log</summary>"
                f"{html_block(runtime.failure_text)}"
                "</details>"
            )
        tool_output_blocks.append(f"<div class=\"subcard\">{header}{html_block(output['output'])}{failure_html}</div>")

    tool_call_blocks: list[str] = []
    for call in tool_calls:
        runtime = tool_events.get(call["call_id"])
        runtime_meta = ""
        if runtime:
            runtime_meta = (
                f"<p class=\"meta\">executed at {html.escape(runtime.timestamp)}"
                f" | tool: {html.escape(runtime.name)}</p>"
            )
        header = (
            f"<p class=\"meta\">call_id: {html.escape(call['call_id'])}"
            f" | name: {html.escape(call['name'])}</p>"
        )
        tool_call_blocks.append(
            f"<div class=\"subcard\">{header}{runtime_meta}{html_block(call['arguments'])}</div>"
        )

    llm_response_blocks = [html_block(message) for message in llm_messages]
    reasoning_blocks = [html_block(reasoning)] if reasoning else []

    raw_request_json = json.dumps(request_payload, indent=2, ensure_ascii=False)
    raw_response_json = json.dumps(response_payload, indent=2, ensure_ascii=False) if response_payload else ""

    details = [
        "<details><summary>Raw request JSON</summary>",
        html_block(raw_request_json),
        "</details>",
    ]
    if raw_response_json:
        details.extend(
            [
                "<details><summary>Raw response JSON</summary>",
                html_block(raw_response_json),
                "</details>",
            ]
        )

    sections = "\n".join(
        section
        for section in [
            render_section("System Prompts", system_blocks),
            render_section("User Prompts", user_blocks),
            render_section("Tool Responses Seen By Model", tool_output_blocks),
            render_section("LLM Reasoning", reasoning_blocks),
            render_section("LLM Tool Calls", tool_call_blocks),
            render_section("LLM Responses", llm_response_blocks),
            "".join(details),
        ]
        if section
    )

    return f"""
<article class="card">
  <h2>Turn {turn.index}</h2>
  <p class="meta">request: {html.escape(turn.request.timestamp)} | response: {html.escape(turn.response.timestamp if turn.response else 'missing')} | model: {html.escape(model)}</p>
  {sections}
</article>
""".strip()


def render_html(turns: list[Turn], source_file: Path, tool_events: dict[str, ToolExecutionEvent]) -> str:
    cards = "\n\n".join(render_turn(turn, tool_events) for turn in turns)
    if not cards:
        cards = "<p>No OpenRouter request or response blocks were found.</p>"

    return f"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>OpenRouter Trace Review</title>
  <style>
    :root {{
      --bg: #f4f1eb;
      --panel: #fffdfa;
      --subpanel: #f8f5ee;
      --text: #221f1b;
      --muted: #6d655b;
      --accent: #0f5f56;
      --border: #ddd2c4;
      --shadow: 0 10px 28px rgba(0, 0, 0, 0.06);
    }}
    * {{ box-sizing: border-box; }}
    body {{
      margin: 0;
      color: var(--text);
      background: radial-gradient(circle at top left, #efe6d6 0, var(--bg) 42%);
      font-family: "Segoe UI", Tahoma, sans-serif;
      line-height: 1.45;
    }}
    main {{ max-width: 1320px; margin: 0 auto; padding: 24px; }}
    h1 {{ margin: 0 0 8px; }}
    h2 {{ margin-top: 0; }}
    h3 {{ margin-bottom: 8px; }}
    .subtitle {{ color: var(--muted); margin: 0 0 20px; }}
    .card {{
      background: var(--panel);
      border: 1px solid var(--border);
      border-radius: 14px;
      box-shadow: var(--shadow);
      padding: 18px;
      margin-bottom: 18px;
    }}
    .subcard {{
      background: var(--subpanel);
      border: 1px solid var(--border);
      border-radius: 10px;
      padding: 12px;
      margin-bottom: 10px;
    }}
    .meta {{ color: var(--muted); margin: 0 0 10px; }}
    pre {{
      margin: 0;
      white-space: pre-wrap;
      overflow-x: auto;
      background: #faf8f2;
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 12px;
      font-family: Consolas, "Courier New", monospace;
      font-size: 13px;
    }}
    section {{ margin-top: 18px; }}
    details {{ margin-top: 12px; }}
    summary {{ cursor: pointer; color: var(--accent); font-weight: 600; }}
    a {{ color: var(--accent); }}
  </style>
</head>
<body>
  <main>
    <h1>OpenRouter Trace Review</h1>
    <p class="subtitle">Source: {html.escape(str(source_file))} | Turns: {len(turns)} | Tool executions: {len(tool_events)}</p>
    {cards}
  </main>
</body>
</html>
"""


def resolve_log_path(path: Path) -> Path:
    if path.is_file():
        return path
    if path.is_dir():
        candidates = sorted(path.glob("trace-*.log"), key=lambda item: item.stat().st_mtime, reverse=True)
        if candidates:
            return candidates[0]
        raise FileNotFoundError(f"No trace-*.log files found under {path}")
    raise FileNotFoundError(f"Path does not exist: {path}")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Generate an HTML review report for OpenRouter trace logs.")
    parser.add_argument(
        "log_path",
        type=Path,
        nargs="?",
        default=DEFAULT_LOGS_DIR,
        help=(
            "Path to a trace log file or a directory containing trace-*.log files "
            f"(default: newest trace log from {DEFAULT_LOGS_DIR})"
        ),
    )
    parser.add_argument(
        "-o",
        "--output",
        type=Path,
        default=Path("openrouter-trace-review.html"),
        help="Output HTML file path (default: openrouter-trace-review.html)",
    )
    parser.add_argument("--open", action="store_true", help="Open the generated report in the default browser")
    return parser


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()

    try:
        log_file = resolve_log_path(args.log_path)
    except FileNotFoundError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1

    try:
        log_text = log_file.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        print(f"ERROR: Could not read log file: {exc}", file=sys.stderr)
        return 1

    requests = parse_json_blocks(log_text, REQUEST_MARKER_RE)
    responses = parse_json_blocks(log_text, RESPONSE_MARKER_RE)
    tool_events = parse_tool_executions(log_text)
    turns = build_turns(requests, responses)
    report = render_html(turns, log_file, tool_events)

    try:
        args.output.write_text(report, encoding="utf-8")
    except OSError as exc:
        print(f"ERROR: Could not write output file: {exc}", file=sys.stderr)
        return 1

    print(f"Generated report: {args.output.resolve()}")
    print(f"Turns found: {len(turns)}")
    print(f"Tool execution events found: {len(tool_events)}")

    if args.open:
        webbrowser.open(args.output.resolve().as_uri())

    return 0


if __name__ == "__main__":
    raise SystemExit(main())