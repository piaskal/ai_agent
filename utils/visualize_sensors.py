#!/usr/bin/env python3
"""Generate a simple HTML dashboard from zipped sensor JSON readings."""

from __future__ import annotations

import argparse
import datetime as dt
import html
import json
import statistics
import sys
import zipfile
from collections import Counter
from dataclasses import dataclass
from pathlib import Path


METRICS: dict[str, dict[str, str]] = {
    "temperature_K": {"label": "Temperature", "unit": "K", "token": "temperature"},
    "pressure_bar": {"label": "Pressure", "unit": "bar", "token": "pressure"},
    "water_level_meters": {"label": "Water Level", "unit": "m", "token": "water"},
    "voltage_supply_v": {"label": "Supply Voltage", "unit": "V", "token": "voltage"},
    "humidity_percent": {"label": "Humidity", "unit": "%", "token": "humidity"},
}


@dataclass
class Reading:
    timestamp: int
    sensor_type: str
    values: dict[str, float]

    @property
    def iso_time(self) -> str:
        return dt.datetime.fromtimestamp(self.timestamp, dt.timezone.utc).isoformat()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Visualize sensor readings stored in a zip archive of JSON files.")
    parser.add_argument(
        "zip_file",
        nargs="?",
        default="sensors.zip",
        type=Path,
        help="Path to sensors zip file (default: sensors.zip)",
    )
    parser.add_argument(
        "-o",
        "--output",
        type=Path,
        default=Path("sensor-dashboard.html"),
        help="Output HTML file path (default: sensor-dashboard.html)",
    )
    parser.add_argument(
        "--max-points",
        type=int,
        default=2500,
        help="Maximum number of points per metric to keep charts responsive (default: 2500)",
    )
    return parser.parse_args()


def metric_is_active(sensor_type: str, token: str) -> bool:
    parts = set(sensor_type.split("/"))
    return token in parts


def load_readings(zip_path: Path) -> list[Reading]:
    if not zip_path.exists():
        raise FileNotFoundError(f"Zip file not found: {zip_path}")

    readings: list[Reading] = []

    with zipfile.ZipFile(zip_path, "r") as archive:
        for name in archive.namelist():
            if not name.lower().endswith(".json"):
                continue

            with archive.open(name, "r") as handle:
                raw = json.loads(handle.read().decode("utf-8"))

            sensor_type = str(raw.get("sensor_type", ""))
            timestamp = int(raw["timestamp"])
            values: dict[str, float] = {}

            for metric in METRICS:
                raw_value = raw.get(metric)
                if raw_value is None:
                    continue
                values[metric] = float(raw_value)

            readings.append(Reading(timestamp=timestamp, sensor_type=sensor_type, values=values))

    readings.sort(key=lambda item: item.timestamp)
    return readings


def downsample(items: list[tuple[str, float]], max_points: int) -> list[tuple[str, float]]:
    if max_points <= 0 or len(items) <= max_points:
        return items

    step = len(items) / max_points
    sampled: list[tuple[str, float]] = []
    index = 0.0
    for _ in range(max_points):
        sampled.append(items[int(index)])
        index += step
    return sampled


def build_metric_series(readings: list[Reading], max_points: int) -> dict[str, list[dict[str, float | str]]]:
    result: dict[str, list[dict[str, float | str]]] = {}

    for metric, meta in METRICS.items():
        token = meta["token"]
        points: list[tuple[str, float]] = []

        for entry in readings:
            if not metric_is_active(entry.sensor_type, token):
                continue
            value = entry.values.get(metric)
            if value is None:
                continue
            points.append((entry.iso_time, value))

        points = downsample(points, max_points)
        result[metric] = [{"x": stamp, "y": value} for stamp, value in points]

    return result


def build_summary(readings: list[Reading], series: dict[str, list[dict[str, float | str]]]) -> dict[str, dict[str, float | int | None]]:
    summary: dict[str, dict[str, float | int | None]] = {
        "total_readings": {"value": len(readings)},
    }

    for metric, points in series.items():
        values = [float(point["y"]) for point in points]
        if not values:
            summary[metric] = {"count": 0, "min": None, "max": None, "mean": None, "median": None}
            continue
        summary[metric] = {
            "count": len(values),
            "min": min(values),
            "max": max(values),
            "mean": statistics.fmean(values),
            "median": statistics.median(values),
        }

    return summary


def render_html(
    source_zip: Path,
    readings: list[Reading],
    type_counts: Counter[str],
    series: dict[str, list[dict[str, float | str]]],
    summary: dict[str, dict[str, float | int | None]],
) -> str:
    title = "Sensor Dashboard"
    series_json = json.dumps(series)
    counts_json = json.dumps(dict(type_counts))
    meta_json = json.dumps(METRICS)

    summary_rows: list[str] = []
    for metric, meta in METRICS.items():
        stats = summary.get(metric, {})
        unit = meta["unit"]
        count = stats.get("count", 0)
        if not count:
            summary_rows.append(
                f"<tr><td>{html.escape(meta['label'])}</td><td>0</td><td>-</td><td>-</td><td>-</td><td>-</td></tr>"
            )
            continue

        summary_rows.append(
            "<tr>"
            f"<td>{html.escape(meta['label'])}</td>"
            f"<td>{int(count)}</td>"
            f"<td>{float(stats['min']):.2f} {unit}</td>"
            f"<td>{float(stats['max']):.2f} {unit}</td>"
            f"<td>{float(stats['mean']):.2f} {unit}</td>"
            f"<td>{float(stats['median']):.2f} {unit}</td>"
            "</tr>"
        )

    first_ts = readings[0].iso_time if readings else "-"
    last_ts = readings[-1].iso_time if readings else "-"

    return f"""<!doctype html>
<html lang=\"en\">
<head>
  <meta charset=\"utf-8\" />
  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />
  <title>{title}</title>
  <script src=\"https://cdn.jsdelivr.net/npm/chart.js@4.4.3\"></script>
  <script src=\"https://cdn.jsdelivr.net/npm/chartjs-adapter-date-fns@3\"></script>
  <style>
    :root {{
      --bg: #f2f7f9;
      --panel: #ffffff;
      --ink: #12303a;
      --soft: #56717a;
      --line: #d7e4ea;
      --accent: #0f766e;
      --accent2: #1d4ed8;
    }}
    * {{ box-sizing: border-box; }}
    body {{
      margin: 0;
      color: var(--ink);
      background:
        radial-gradient(circle at 0% 0%, #dff3f0 0%, transparent 38%),
        radial-gradient(circle at 100% 0%, #e4ecff 0%, transparent 34%),
        var(--bg);
      font-family: "Segoe UI", Tahoma, sans-serif;
    }}
    .wrap {{ max-width: 1200px; margin: 0 auto; padding: 20px; }}
    h1 {{ margin: 0 0 8px; font-size: 2rem; }}
    .meta {{ color: var(--soft); margin: 0 0 16px; }}
    .grid {{ display: grid; gap: 14px; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); }}
    .card {{
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: 14px;
      padding: 14px;
      box-shadow: 0 8px 22px rgba(18, 48, 58, 0.08);
    }}
    .kpi {{ font-size: 1.6rem; margin: 6px 0 0; font-weight: 700; }}
    .charts {{ display: grid; gap: 14px; margin-top: 14px; }}
    table {{ width: 100%; border-collapse: collapse; }}
    th, td {{ border-bottom: 1px solid var(--line); text-align: left; padding: 8px 6px; font-size: 0.92rem; }}
    th {{ color: var(--soft); font-weight: 600; }}
    .chart-box {{ height: 330px; }}
    .chart-box canvas {{ width: 100% !important; height: 100% !important; }}
  </style>
</head>
<body>
  <main class=\"wrap\">
    <h1>{title}</h1>
    <p class=\"meta\">Source: {html.escape(str(source_zip))}</p>

    <section class=\"grid\">
      <article class=\"card\">
        <h2>Total Readings</h2>
        <p class=\"kpi\">{len(readings)}</p>
      </article>
      <article class=\"card\">
        <h2>First Timestamp (UTC)</h2>
        <p class=\"kpi\">{html.escape(first_ts)}</p>
      </article>
      <article class=\"card\">
        <h2>Last Timestamp (UTC)</h2>
        <p class=\"kpi\">{html.escape(last_ts)}</p>
      </article>
    </section>

    <section class=\"card\" style=\"margin-top:14px\">
      <h2>Metric Summary</h2>
      <table>
        <thead>
          <tr><th>Metric</th><th>Count</th><th>Min</th><th>Max</th><th>Mean</th><th>Median</th></tr>
        </thead>
        <tbody>
          {''.join(summary_rows)}
        </tbody>
      </table>
    </section>

    <section class=\"charts\" id=\"charts\"></section>
    <section class=\"card\">
      <h2>Sensor Type Distribution</h2>
      <div class=\"chart-box\"><canvas id=\"typeChart\"></canvas></div>
    </section>
  </main>

  <script>
    const metricSeries = {series_json};
    const metricMeta = {meta_json};
    const typeCounts = {counts_json};

    const palette = ["#0f766e", "#1d4ed8", "#ea580c", "#9333ea", "#16a34a", "#b45309"];

    const chartsContainer = document.getElementById("charts");
    let colorIdx = 0;

    for (const [metric, points] of Object.entries(metricSeries)) {{
      const card = document.createElement("section");
      card.className = "card";
      card.innerHTML = `
        <h2>${{metricMeta[metric].label}} (${{metricMeta[metric].unit}})</h2>
        <div class=\"chart-box\"><canvas id=\"chart-${{metric}}\"></canvas></div>
      `;
      chartsContainer.appendChild(card);

      const color = palette[colorIdx % palette.length];
      colorIdx += 1;

      new Chart(document.getElementById(`chart-${{metric}}`), {{
        type: "line",
        data: {{
          datasets: [{{
            label: metricMeta[metric].label,
            data: points,
            borderColor: color,
            backgroundColor: color,
            tension: 0.15,
            pointRadius: 0,
            borderWidth: 1.8,
          }}],
        }},
        options: {{
          maintainAspectRatio: false,
          animation: false,
          interaction: {{ mode: "nearest", intersect: false }},
          scales: {{
            x: {{ type: "time", time: {{ tooltipFormat: "yyyy-MM-dd HH:mm:ss" }} }},
            y: {{ title: {{ display: true, text: metricMeta[metric].unit }} }},
          }},
        }},
      }});
    }}

    new Chart(document.getElementById("typeChart"), {{
      type: "bar",
      data: {{
        labels: Object.keys(typeCounts),
        datasets: [{{
          label: "Readings",
          data: Object.values(typeCounts),
          backgroundColor: "#1d4ed8",
        }}],
      }},
      options: {{
        maintainAspectRatio: false,
        animation: false,
        plugins: {{ legend: {{ display: false }} }},
        scales: {{
          x: {{ ticks: {{ maxRotation: 75, minRotation: 45 }} }},
          y: {{ beginAtZero: true }},
        }},
      }},
    }});
  </script>
</body>
</html>
"""


def main() -> int:
    args = parse_args()

    try:
        readings = load_readings(args.zip_file)
    except (FileNotFoundError, OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1

    if not readings:
        print("ERROR: No JSON readings were found in the ZIP file.", file=sys.stderr)
        return 1

    type_counts = Counter(item.sensor_type for item in readings)
    series = build_metric_series(readings, args.max_points)
    summary = build_summary(readings, series)
    html_output = render_html(args.zip_file, readings, type_counts, series, summary)

    try:
        args.output.write_text(html_output, encoding="utf-8")
    except OSError as exc:
        print(f"ERROR: Could not write output file: {exc}", file=sys.stderr)
        return 1

    print(f"Dashboard generated: {args.output.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
