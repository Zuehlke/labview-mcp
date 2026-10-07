#!/usr/bin/env python3
"""Snapshot the repository's download and traffic numbers into CSV files.

Run daily by .github/workflows/download-stats.yml against a checkout of the `stats` branch.
Standard library only, so the workflow needs no install step.

Why each file exists:

  releases.csv      GitHub keeps a cumulative `download_count` per release asset FOR EVER, and
                    that is the real install signal: every `claude plugin install` / `update`
                    fetches /releases/latest/download/labview-mcp.zip, and a manual download
                    lands on labview-mcp.zip or labview-mcp-v<x.y.z>.zip. One row per asset per
                    snapshot; daily deltas are computed by the page, never stored.

  traffic.csv       views and clones are only kept by GitHub for 14 days, so they are UPSERTED
                    by date - every run re-reads the last 14 days and the overlap deduplicates.
                    Needs a token with Administration: read (GITHUB_TOKEN cannot be granted it),
                    so it is skipped, and says so, when STATS_TRAFFIC_TOKEN is not set.

  ci_downloads.csv  our OWN workflows download the zip: release.yml's last step checks the
                    release it just published, and verify-release.yml checks /releases/latest
                    every morning. Each such run fetches labview-mcp.zip, labview-mcp.sha256 and
                    labview-mcp.manifest.sha256 once per attempt (never the versioned zip).
                    Recorded per run so the page can subtract them instead of guessing.
"""

from __future__ import annotations

import csv
import json
import os
import sys
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

API = "https://api.github.com"
REPO = os.environ.get("GITHUB_REPOSITORY", "Zuehlke/labview-mcp")
DATA = Path(sys.argv[1] if len(sys.argv) > 1 else "data")

RELEASE_FIELDS = ["snapshot_utc", "tag", "published_at", "asset", "download_count"]
TRAFFIC_FIELDS = ["date", "views", "views_unique", "clones", "clones_unique"]
CI_FIELDS = ["run_id", "workflow", "created_at", "event", "conclusion", "run_attempt", "tag"]

# The workflows whose runs download release assets, and how each picks its target.
CI_WORKFLOWS = {"release.yml": "own-tag", "verify-release.yml": "latest"}


def get(path: str, token: str | None) -> tuple[object, dict]:
    req = urllib.request.Request(API + path)
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("User-Agent", "labview-mcp-download-stats")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    with urllib.request.urlopen(req, timeout=60) as resp:
        return json.load(resp), dict(resp.headers)


def get_all(path: str, token: str | None, key: str | None = None) -> list:
    """Follow `Link: rel=next` pages. `key` names the list inside an object answer."""
    items: list = []
    sep = "&" if "?" in path else "?"
    url = f"{path}{sep}per_page=100"
    while url:
        body, headers = get(url, token)
        items.extend(body[key] if key else body)
        url = None
        for part in headers.get("Link", "").split(","):
            if 'rel="next"' in part:
                url = part[part.index("<") + 1 : part.index(">")].replace(API, "")
    return items


def read_rows(path: Path) -> list[dict]:
    if not path.exists():
        return []
    with path.open(newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def write_rows(path: Path, fields: list[str], rows: list[dict]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=fields, lineterminator="\n")
        w.writeheader()
        w.writerows(rows)


def snapshot_releases(token: str | None, now: str) -> tuple[list[dict], list[dict]]:
    releases = get_all(f"/repos/{REPO}/releases", token)
    rows = []
    for rel in releases:
        if rel.get("draft"):
            continue
        for asset in rel["assets"]:
            rows.append({
                "snapshot_utc": now,
                "tag": rel["tag_name"],
                "published_at": rel["published_at"],
                "asset": asset["name"],
                "download_count": asset["download_count"],
            })
    existing = read_rows(DATA / "releases.csv")
    write_rows(DATA / "releases.csv", RELEASE_FIELDS, existing + rows)
    return rows, releases


def latest_tag_at(when: str, releases: list[dict]) -> str:
    """The release /releases/latest returned at `when`: newest non-draft, non-prerelease."""
    candidates = [r for r in releases
                  if not r.get("draft") and not r.get("prerelease") and r["published_at"] <= when]
    return max(candidates, key=lambda r: r["published_at"])["tag_name"] if candidates else ""


def snapshot_ci(token: str | None, releases: list[dict]) -> int:
    known = {r["run_id"]: r for r in read_rows(DATA / "ci_downloads.csv")}
    added = 0
    for workflow, target in CI_WORKFLOWS.items():
        try:
            # No `status=completed` query: measured 2026-10-07, that filter returned 16 of 44
            # release.yml runs, all older than a month. Filter here instead.
            runs = get_all(f"/repos/{REPO}/actions/workflows/{workflow}/runs",
                           token, key="workflow_runs")
        except urllib.error.HTTPError as e:
            print(f"ci: {workflow}: HTTP {e.code}, skipped")
            continue
        for run in runs:
            if run["status"] != "completed":
                continue
            run_id = str(run["id"])
            # A cancelled or skipped run may never have reached the download step; a failed
            # one did and may have retried, which run_attempt does not capture (the retries are
            # inside one attempt). Recorded either way; the page counts success + failure.
            tag = run["head_branch"] if target == "own-tag" else latest_tag_at(run["created_at"], releases)
            row = {
                "run_id": run_id,
                "workflow": workflow,
                "created_at": run["created_at"],
                "event": run["event"],
                "conclusion": run["conclusion"] or "",
                "run_attempt": run.get("run_attempt", 1),
                "tag": tag,
            }
            if run_id not in known:
                added += 1
            known[run_id] = row
    rows = sorted(known.values(), key=lambda r: r["created_at"])
    write_rows(DATA / "ci_downloads.csv", CI_FIELDS, rows)
    return added


def snapshot_traffic(token: str | None) -> str:
    if not token:
        return "skipped: STATS_TRAFFIC_TOKEN is not set (traffic needs Administration: read)"
    try:
        views, _ = get(f"/repos/{REPO}/traffic/views", token)
        clones, _ = get(f"/repos/{REPO}/traffic/clones", token)
    except urllib.error.HTTPError as e:
        return f"skipped: HTTP {e.code} - the token lacks Administration: read on {REPO}?"
    by_date = {r["date"]: r for r in read_rows(DATA / "traffic.csv")}
    for kind, body in (("views", views), ("clones", clones)):
        for day in body[kind]:
            date = day["timestamp"][:10]
            row = by_date.setdefault(date, {f: "" for f in TRAFFIC_FIELDS} | {"date": date})
            row[kind] = day["count"]
            row[f"{kind}_unique"] = day["uniques"]
    rows = sorted(by_date.values(), key=lambda r: r["date"])
    write_rows(DATA / "traffic.csv", TRAFFIC_FIELDS, rows)
    return f"{len(views['views'])} view days, {len(clones['clones'])} clone days upserted"


def main() -> int:
    now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    token = os.environ.get("GITHUB_TOKEN")
    traffic_token = os.environ.get("STATS_TRAFFIC_TOKEN") or None

    rows, releases = snapshot_releases(token, now)
    total = sum(int(r["download_count"]) for r in rows if r["asset"].endswith(".zip"))
    print(f"releases: {len(releases)} releases, {len(rows)} assets, {total} zip downloads in total")
    print(f"ci: {snapshot_ci(token, releases)} new run(s) recorded")
    print(f"traffic: {snapshot_traffic(traffic_token)}")

    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write(f"### Download stats {now}\n\n{total} zip downloads across "
                    f"{len(releases)} releases (raw, CI downloads not subtracted).\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
