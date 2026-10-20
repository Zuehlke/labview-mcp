#!/usr/bin/env python3
"""Assemble the GitHub Pages site: a landing page, the download-stats dashboard and every
diagram in docs/diagrams.

Run by .github/workflows/pages.yml as `build.py <stats data dir> <output dir>`; locally, any
empty or missing data dir gives a site whose stats page reports no data. Standard library only.

Every docs/diagrams/*.html is published and listed by its <title>, so a new diagram appears on
the landing page without editing anything here. The archify receipts and source .json beside
them are not published.
"""
import html
import re
import shutil
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
ITEMS_MARKER = "<!-- ITEMS -->"


def title_of(page: Path) -> str:
    # The first <title> is the page's; later ones belong to SVG nodes inside the diagram.
    found = re.search(r"<title>(.*?)</title>", page.read_text(encoding="utf-8"), re.S)
    name = html.unescape(found.group(1)).strip() if found else page.stem
    return re.sub(r"\s+Diagram$", "", name)


def main(data_dir: str, out_dir: str) -> None:
    out = Path(out_dir)
    shutil.rmtree(out, ignore_errors=True)

    stats = out / "stats"
    stats.mkdir(parents=True)
    shutil.copy(REPO / ".github/stats/site/index.html", stats / "index.html")
    (stats / "data").mkdir()
    source = Path(data_dir)
    # No symlinks, at either level: one committed to the stats branch must not publish what it
    # points at - .git/config holds the checkout's token.
    if source.is_symlink():
        sys.exit(f"build.py: {data_dir} is a symlink; refusing to publish through it")
    if source.is_dir():
        for f in source.iterdir():
            if f.is_file() and not f.is_symlink():
                shutil.copy(f, stats / "data" / f.name)

    diagrams = sorted((REPO / "docs/diagrams").glob("*.html"))
    (out / "diagrams").mkdir()
    for page in diagrams:
        shutil.copy(page, out / "diagrams" / page.name)

    items = [("stats/", "Download statistics", "Daily release-asset downloads and repository traffic")]
    items += [(f"diagrams/{p.name}", title_of(p), "Diagram")
              for p in diagrams]
    rows = "\n".join(
        f'    <li><a href="{html.escape(href)}">{html.escape(name)}</a><span>{html.escape(note)}</span></li>'
        for href, name, note in items)
    template = (REPO / ".github/site/index.html").read_text(encoding="utf-8")
    if ITEMS_MARKER not in template:
        sys.exit(f"build.py: {ITEMS_MARKER} is missing from .github/site/index.html")
    (out / "index.html").write_text(template.replace(ITEMS_MARKER, rows), encoding="utf-8")
    print(f"site: {len(items)} item(s) in {out}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit("usage: build.py <stats data dir> <output dir>")
    main(sys.argv[1], sys.argv[2])
