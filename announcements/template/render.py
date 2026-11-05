"""Render a news poster from a content file into PNG and PDF.

    python render.py <content.json> [--out <folder>]

The content file holds every text; poster.html holds the look. The output files are named after
the content file (news-2026-11.json -> news-2026-11.png / .pdf) and land beside it unless --out
names another folder. Needs Python 3, Microsoft Edge or Google Chrome, and the `segno` package
for the QR code (python -m pip install --user segno). Internet access is needed for the fonts.
"""
import argparse, html, json, pathlib, shutil, string, subprocess, sys, tempfile

HERE = pathlib.Path(__file__).resolve().parent
BROWSERS = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
]
TICK = ('<svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="#1f1f1e" stroke-width="3" '
        'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M4.5 12.5l4.5 4.5L19.5 7"></path></svg>')
TILTS = [-1.4, 1.2, -0.8]
TAPE_TILTS = [2, -3, 1.5]


def esc(s):
    return html.escape(str(s), quote=True)


def darker(hex_color, f):
    h = hex_color.lstrip("#")
    r, g, b = (int(h[i:i + 2], 16) for i in (0, 2, 4))
    return "#%02x%02x%02x" % tuple(max(0, min(255, int(c * f))) for c in (r, g, b))


def note_html(i, n):
    color = n.get("color", "#fff3c4")
    parts = [
        '<div class="label mono"><span class="badge">%02d</span><span>%s</span></div>' % (i + 1, esc(n["label"])),
    ]
    if n.get("title"):
        parts.append('<div class="big">%s</div>' % esc(n["title"]))
    if n.get("chips"):
        parts.append('<div class="chips">%s</div>'
                     % "".join('<span class="chip mono">%s</span>' % esc(c) for c in n["chips"]))
    if n.get("checks"):
        parts.append('<div class="checks">%s</div>'
                     % "".join('<div class="check"><span class="tick">%s</span>%s</div>' % (TICK, esc(c))
                               for c in n["checks"]))
    if n.get("text"):
        parts.append('<p class="text">%s</p>' % esc(n["text"]))
    fold = ('<div class="fold" style="background: linear-gradient(to top left, transparent 50%%, %s 50%%, %s 100%%)"></div>'
            % (darker(color, 0.82), darker(color, 0.95)))
    return ('<div class="note" style="transform: rotate(%sdeg)">\n'
            '<div class="paper" style="background: %s">\n%s\n%s\n</div>\n'
            '<div class="tape" style="transform: rotate(%sdeg)"></div>\n</div>'
            % (TILTS[i % 3], color, "\n".join(parts), fold, TAPE_TILTS[i % 3]))


def qr_svg(url):
    try:
        import segno
    except ImportError:
        sys.exit("The QR code needs the segno package:  python -m pip install --user segno")
    import io
    buf = io.BytesIO()
    segno.make(url, error="m").save(buf, kind="svg", scale=10, border=0, dark="#141413", light=None,
                                    xmldecl=False, svgns=True)
    svg = buf.getvalue().decode()
    w = svg.split('width="')[1].split('"')[0]
    return svg.replace('<svg ', '<svg viewBox="0 0 %s %s" ' % (w, w), 1)


def find_browser():
    for b in BROWSERS:
        if pathlib.Path(b).exists():
            return b
    for name in ("msedge", "chrome", "google-chrome", "chromium"):
        p = shutil.which(name)
        if p:
            return p
    sys.exit("Neither Microsoft Edge nor Google Chrome was found.")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("content")
    ap.add_argument("--out")
    a = ap.parse_args()

    content_path = pathlib.Path(a.content).resolve()
    c = json.loads(content_path.read_text(encoding="utf-8"))
    out_dir = pathlib.Path(a.out).resolve() if a.out else content_path.parent
    out_dir.mkdir(parents=True, exist_ok=True)
    stem = content_path.stem

    image = pathlib.Path(c.get("image", "sketch.webp"))
    if not image.is_absolute():
        image = (content_path.parent / image) if (content_path.parent / image).exists() else HERE / image
    if not image.exists():
        sys.exit("Image not found: %s" % image)

    notes = c["notes"]
    if not 1 <= len(notes) <= 3:
        sys.exit("A poster holds 1 to 3 notes, this one has %d." % len(notes))

    page = string.Template((HERE / "poster.html").read_text(encoding="utf-8")).substitute(
        title=esc(c.get("title", "News")),
        accent=esc(c.get("accent", "#f2b705")),
        cols=len(notes),
        brand=esc(c.get("brand", ">_ LabVIEW MCP")),
        tagline=esc(c.get("tagline", "")),
        headline_top=esc(c["headline_top"]),
        headline_marked=esc(c["headline_marked"]),
        aside=esc(c.get("aside", "")),
        image=image.as_uri(),
        image_alt=esc(c.get("image_alt", "")),
        notes="\n\n".join(note_html(i, n) for i, n in enumerate(notes)),
        cta=esc(c.get("cta", "Try it now")),
        url=esc(c["url"]),
        url_text=esc(c.get("url_text", c["url"].split("://", 1)[-1])),
        cta_note=esc(c.get("cta_note", "Scan the QR code \u2192")),
        qr_svg=qr_svg(c["url"]),
    )

    browser = find_browser()
    with tempfile.TemporaryDirectory() as tmp:
        page_file = pathlib.Path(tmp) / "poster.html"
        page_file.write_text(page, encoding="utf-8")
        uri = page_file.as_uri()
        common = [browser, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--virtual-time-budget=8000",
                  "--user-data-dir=" + str(pathlib.Path(tmp) / "profile")]
        png, pdf = out_dir / (stem + ".png"), out_dir / (stem + ".pdf")
        subprocess.run(common + ["--force-device-scale-factor=2", "--window-size=1131,1600",
                                 "--screenshot=" + str(png), uri], check=True, capture_output=True)
        subprocess.run(common + ["--no-pdf-header-footer", "--print-to-pdf=" + str(pdf), uri],
                       check=True, capture_output=True)
        if c.get("keep_html"):
            shutil.copy(page_file, out_dir / (stem + ".html"))
    for f in (png, pdf):
        if not f.exists() or f.stat().st_size == 0:
            sys.exit("Rendering failed: %s was not written." % f)
        print("written", f)


if __name__ == "__main__":
    main()
