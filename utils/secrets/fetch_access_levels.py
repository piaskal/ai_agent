import argparse
import csv
import json
import os
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime
from pathlib import Path
from typing import Any

API_URL = "https://hub.ag3nts.org/api/accesslevel"


def parse_args() -> argparse.Namespace:
    script_dir = Path(__file__).resolve().parent
    default_csv = script_dir.parent / "data" / "people.csv"
    default_cache = script_dir / "access_levels_cache.json"

    parser = argparse.ArgumentParser(
        description="Fetch access level for every person from a CSV file."
    )
    parser.add_argument(
        "--csv",
        default=str(default_csv),
        help="Path to input CSV file (default: utils/data/people.csv).",
    )
    parser.add_argument(
        "--api-key",
        default=None,
        help="API key. If omitted, AG3NTS_API_KEY or HUB_API_KEY env var is used.",
    )
    parser.add_argument(
        "--timeout",
        type=int,
        default=20,
        help="HTTP timeout in seconds (default: 20).",
    )
    parser.add_argument(
        "--highest-only",
        action="store_true",
        help="Print only people with the highest access level found.",
    )
    parser.add_argument(
        "--cache",
        default=str(default_cache),
        help="Path to cache file for saved access-level responses.",
    )
    parser.add_argument(
        "--min-delay",
        type=float,
        default=2.0 ,
        help="Minimum delay in seconds between live HTTP requests (default: 2.0).",
    )
    parser.add_argument(
        "--max-retries",
        type=int,
        default=5,
        help="Maximum number of retries for HTTP 429 responses (default: 5).",
    )
    parser.add_argument(
        "--retry-delay",
        type=float,
        default=10.0,
        help="Base retry delay in seconds for HTTP 429 responses (default: 10.0).",
    )
    return parser.parse_args()


def get_api_key(cli_value: str | None) -> str:
    if cli_value:
        return cli_value

    env_value = os.getenv("AG3NTS_API_KEY") or os.getenv("HUB_API_KEY")
    if env_value:
        return env_value

    raise ValueError(
        "Missing API key. Pass --api-key or set AG3NTS_API_KEY/HUB_API_KEY environment variable."
    )


def birth_year_from_birth_date(value: str) -> int:
    # Handles dates in YYYY-MM-DD format from people.csv.
    return datetime.strptime(value, "%Y-%m-%d").year


def normalize_row_keys(row: dict[str, Any]) -> dict[str, Any]:
    # Removes BOM and surrounding whitespace from CSV headers.
    return {str(key).lstrip("\ufeff").strip(): value for key, value in row.items()}


def make_person_key(name: str, surname: str, birth_year: int) -> str:
    return f"{name}|{surname}|{birth_year}"


def load_cache(cache_path: Path) -> dict[str, dict[str, Any]]:
    if not cache_path.exists():
        return {}

    with cache_path.open("r", encoding="utf-8") as f:
        data = json.load(f)

    if not isinstance(data, dict):
        raise ValueError(f"Cache file has invalid format: {cache_path}")

    return {
        str(key): value
        for key, value in data.items()
        if isinstance(key, str) and isinstance(value, dict)
    }


def save_cache(cache_path: Path, cache_data: dict[str, dict[str, Any]]) -> None:
    cache_path.parent.mkdir(parents=True, exist_ok=True)
    with cache_path.open("w", encoding="utf-8") as f:
        json.dump(cache_data, f, ensure_ascii=False, indent=2)
        f.write("\n")


def flush_cache_if_needed(
    cache_path: Path, cache_data: dict[str, dict[str, Any]], cache_updated: bool
) -> bool:
    if cache_updated:
        save_cache(cache_path, cache_data)
        return False
    return cache_updated


class HttpRequestFailedError(Exception):
    def __init__(self, status_code: int, details: str):
        super().__init__(f"HTTP {status_code}")
        self.status_code = status_code
        self.details = details


def call_access_level_api(
    api_key: str, name: str, surname: str, birth_year: int, timeout: int
) -> Any:
    payload = {
        "apikey": api_key,
        "name": name,
        "surname": surname,
        "birthYear": birth_year,
    }

    body = json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(
        API_URL,
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )

    with urllib.request.urlopen(request, timeout=timeout) as response:
        response_text = response.read().decode("utf-8")

    try:
        return json.loads(response_text)
    except json.JSONDecodeError:
        return response_text


def print_progress(message: str) -> None:
    print(message, file=sys.stderr)


def get_access_level_with_retry(
    api_key: str,
    name: str,
    surname: str,
    birth_year: int,
    timeout: int,
    min_delay: float,
    max_retries: int,
    retry_delay: float,
    next_request_at: float,
) -> tuple[Any, float]:
    now = time.monotonic()
    if now < next_request_at:
        wait_seconds = next_request_at - now
        print_progress(
            f"Waiting {wait_seconds:.1f}s before next request for {name} {surname}"
        )
        time.sleep(wait_seconds)

    attempt = 0
    while True:
        try:
            result = call_access_level_api(api_key, name, surname, birth_year, timeout)
            return result, time.monotonic() + min_delay
        except urllib.error.HTTPError as ex:
            details = ex.read().decode("utf-8", errors="replace")
            if ex.code == 429 and attempt < max_retries:
                backoff_seconds = retry_delay * (2 ** attempt)
                print_progress(
                    f"Rate limited for {name} {surname}; retry {attempt + 1}/{max_retries} in {backoff_seconds:.1f}s"
                )
                time.sleep(backoff_seconds)
                attempt += 1
                continue

            raise HttpRequestFailedError(ex.code, details) from ex


def extract_access_level(result: Any) -> int | None:
    if isinstance(result, dict):
        access_level = result.get("accessLevel")
        if isinstance(access_level, int):
            return access_level
    return None


def main() -> int:
    args = parse_args()
    successful_results: list[dict[str, Any]] = []
    cache_path = Path(args.cache)
    try:
        cache = load_cache(cache_path)
    except Exception as ex:
        print(f"Failed to load cache file: {ex}", file=sys.stderr)
        return 2

    cache_updated = False
    new_cache_entries = 0
    next_request_at = time.monotonic()
    api_key: str | None = None

    csv_path = Path(args.csv)
    if not csv_path.exists():
        print(f"CSV file not found: {csv_path}", file=sys.stderr)
        return 2

    # utf-8-sig handles BOM-prefixed files where first header may be '\ufeffname'.
    with csv_path.open("r", encoding="utf-8-sig", newline="") as f:
        reader = csv.DictReader(f)
        rows = list(reader)

        try:
            for person_index, row in enumerate(rows, start=1):
                row_index = person_index + 1
                try:
                    row = normalize_row_keys(row)
                    name = (row.get("name") or "").strip()
                    surname = (row.get("surname") or "").strip()
                    birth_date = (row.get("birthDate") or "").strip()

                    display_name = f"{name} {surname}".strip() or "<unknown>"
                    print_progress(
                        f"Processing person {person_index}/{len(rows)}: {display_name}"
                    )

                    if not name or not surname or not birth_date:
                        raise ValueError("missing required columns: name/surname/birthDate")

                    birth_year = birth_year_from_birth_date(birth_date)
                    cache_key = make_person_key(name, surname, birth_year)

                    if cache_key in cache:
                        output = cache[cache_key]
                    else:
                        if api_key is None:
                            try:
                                api_key = get_api_key(args.api_key)
                            except ValueError as ex:
                                print(str(ex), file=sys.stderr)
                                return 2

                        result, next_request_at = get_access_level_with_retry(
                            api_key=api_key,
                            name=name,
                            surname=surname,
                            birth_year=birth_year,
                            timeout=args.timeout,
                            min_delay=args.min_delay,
                            max_retries=args.max_retries,
                            retry_delay=args.retry_delay,
                            next_request_at=next_request_at,
                        )

                        output = {
                            "name": name,
                            "surname": surname,
                            "birthYear": birth_year,
                            "result": result,
                        }
                        cache[cache_key] = output
                        cache_updated = True
                        new_cache_entries += 1

                        if new_cache_entries >= 1:
                            cache_updated = flush_cache_if_needed(
                                cache_path, cache, cache_updated
                            )
                            new_cache_entries = 0

                    successful_results.append(output)
                    if not args.highest_only:
                        print(json.dumps(output, ensure_ascii=False))
                except HttpRequestFailedError as ex:
                    print(
                        json.dumps(
                            {
                                "line": row_index,
                                "name": row.get("name"),
                                "surname": row.get("surname"),
                                "error": f"HTTP {ex.status_code}",
                                "details": ex.details,
                            },
                            ensure_ascii=False,
                        ),
                        file=sys.stderr,
                    )
                except urllib.error.URLError as ex:
                    print(
                        json.dumps(
                            {
                                "line": row_index,
                                "name": row.get("name"),
                                "surname": row.get("surname"),
                                "error": f"Network error: {ex.reason}",
                            },
                            ensure_ascii=False,
                        ),
                        file=sys.stderr,
                    )
                except Exception as ex:
                    print(
                        json.dumps(
                            {
                                "line": row_index,
                                "name": row.get("name"),
                                "surname": row.get("surname"),
                                "error": str(ex),
                            },
                            ensure_ascii=False,
                        ),
                        file=sys.stderr,
                    )
        except KeyboardInterrupt:
            cache_updated = flush_cache_if_needed(cache_path, cache, cache_updated)
            print_progress("Interrupted. Cache saved; rerun will resume from cached entries.")
            return 130

    cache_updated = flush_cache_if_needed(cache_path, cache, cache_updated)

    highest_level = None
    highest_people: list[dict[str, Any]] = []

    for item in successful_results:
        access_level = extract_access_level(item.get("result"))
        if access_level is None:
            continue

        if highest_level is None or access_level > highest_level:
            highest_level = access_level
            highest_people = [item]
        elif access_level == highest_level:
            highest_people.append(item)

    if highest_level is not None:
        summary = {
            "highestAccessLevel": highest_level,
            "people": [
                {
                    "name": item["name"],
                    "surname": item["surname"],
                    "birthYear": item["birthYear"],
                }
                for item in highest_people
            ],
        }
        print(json.dumps(summary, ensure_ascii=False))

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
