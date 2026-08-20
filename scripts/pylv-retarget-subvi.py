"""Point a VI's subVI Call at a different subVI, in a pylabview-extracted bundle.

WHY THIS EXISTS - the slot pattern. AIXML cannot author a Timed Loop or an Event
Structure at all (`Error 53, Unsupported node type`), and pylabview can edit an
object heap but cannot compose one: no new nodes, no new wires. So logic inside
one of those constructs looked unreachable.

It is not, once a person has put ONE subVI Call inside the construct in the IDE.
That Call is a socket. AIXML authors the plug - a subVI, with no restrictions on
what is inside it - and this script swaps which plug is in the socket. Measured
2026-08-22: a Timed Loop's Call retargeted from alternate.vi to alternate2.vi,
confirmed by LabVIEW's own AIXML export reading target="alternate2.vi", with the
loop, its Timeout/Period, the stop button and the indicator all untouched.

So the IDE is needed once per socket, never again for what goes in it.

WHERE THE NAME LIVES - one pair per link record, plus a caption:
  main .xml   <LinkSaveQualName><String>NAME</String>      the link
  main .xml   <LinkSavePathRef ...>one <String> PER PATH SEGMENT, name last   the path
  _BDHb.xml   <text>"NAME"</text>                          the node's caption

THE PATH IS A SEGMENT LIST, NOT A NAME. This mattered and cost a session's worth of
hand-rolled substitutions: a vi.lib dependency reads

  <LinkSavePathRef Ident="PTH0" TpVal="0">
    <String>&lt;vilib&gt;</String><String>Waveform</String>
    <String>WDTFileIO.llb</String><String>Export Waveforms To Spreadsheet File (1D).vi</String>

- symbolic root, folders, and only the LAST segment is the file name. This script used
to match `<String/>`-then-name, which is the shape of a bare relative path, so on any
real vi.lib target it found the two qualified names and ZERO paths and aborted with
"a link record is half-written". Nothing was half-written; the pattern was too narrow.

So: swapping a plug that lives in the SAME folder as the old one needs only the last
segment rewritten (the default). Pointing a link at a VI somewhere else - the usual
case when the new plug is your own code and the old one was NI's - needs the whole
segment list replaced, which is what --path is for.

There can be MORE THAN ONE link record for the same subVI - a bundle carried one
(IUVI) right after a retarget and two (IUVI plus VIVI) once LabVIEW had saved the
VI - so the pairs are counted, never assumed. The caption is cosmetic, but a
stale one makes the diagram lie, so it is rewritten too.

WHY THE REPLACE IS ELEMENT-SCOPED AND NOT A TEXT REPLACE: the VI's own name can
CONTAIN the subVI's name. `tl_cycle.vi` contains `cycle.vi`, and the VI name sits
in the RSRC/LVSR header, so a bare replace renamed the VI itself. That is a
silent identity change, which is why matching is anchored to the link elements.

A POLYMORPHIC target needs TWO runs, and the tell is a caption count of 0. A call to a
polymorphic VI stores the WRAPPER's name in the diagram caption and the chosen INSTANCE
in its own link record, so retargeting the instance alone rewrites the link and leaves
the caption naming NI's wrapper - the diagram then lies about what it calls. Run the
script once per name, instance and wrapper, and check that between them the caption count
reaches 1. Measured while swapping `Export Waveforms To Spreadsheet File (1D).vi`
(instance, caption 0) and `Export Waveforms to Spreadsheet File.vi` (wrapper, caption 1).

THE ONE CONSTRAINT: the new subVI must keep the connector pane contract - same
terminal names and types. The wires in the heap bind to the pane, so a different
pane leaves them dangling. Verify with lvai_connector_pane on both VIs before
swapping, and always AIXML-export the rebuilt VI afterwards to see what LabVIEW
made of it.

usage: pylv-retarget-subvi.py <main.xml> <heap_BDHb.xml> <old.vi> <new.vi> [--path <C:\dir\new.vi>]
       pylv-retarget-subvi.py <main.xml> <heap_BDHb.xml> --list
"""
import os
import re
import sys


PATH_BLOCK = re.compile(
    r"(<LinkSavePathRef\b[^>]*>)"
    r"((?:\s*<String\s*/>|\s*<String>[^<]*</String>)*)"
    r"(\s*</LinkSavePathRef>)", re.S)
SEGMENT = re.compile(r"<String\s*/>|<String>([^<]*)</String>")


def targets(main_text):
    """Every subVI name the link table mentions, in document order."""
    return re.findall(r"<LinkSaveQualName>\s*<String>([^<]+)</String>", main_text)


def segments(body):
    """The path's segments AS THEY ARE STORED - still XML-escaped, empty <String/> as ''.

    Left escaped on purpose. A symbolic root is written `&lt;vilib&gt;`, and decoding it here
    only to re-encode it on the way out turned it into `&amp;lt;vilib&amp;gt;` - a segment
    literally named "&lt;vilib&gt;", which resolves to nothing. Segments that come from the
    FILE are copied through untouched; only a segment this script invents gets escaped.
    """
    return [(m.group(1) or "") for m in SEGMENT.finditer(body)]


def render_segments(parts, indent):
    return "".join("\n%s<String>%s</String>" % (indent, p) for p in parts)


def escape(text):
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def retarget_paths(main_text, old, new, new_path):
    """Rewrite every LinkSavePathRef whose LAST segment is `old`.

    Anchored on the last segment because that is the file name; the ones before it are
    the symbolic root and the folders, and rewriting those blindly is how a link ends up
    pointing at a directory that does not exist.
    """
    count = 0

    def one(m):
        nonlocal count
        parts = segments(m.group(2))
        if not parts or parts[-1] != old:
            return m.group(0)
        count += 1
        indent = re.search(r"\n(\s*)<String", m.group(2))
        indent = indent.group(1) if indent else "            "
        # `parts` came from the file and is already escaped; new segments are not.
        parts = [escape(q) for q in new_path] if new_path else parts[:-1] + [escape(new)]
        return m.group(1) + render_segments(parts, indent) + m.group(3)

    return PATH_BLOCK.sub(one, main_text), count


def main():
    if len(sys.argv) < 4:
        sys.exit(__doc__.strip().rsplit("usage:", 1)[-1].strip())

    main_path, heap_path = sys.argv[1], sys.argv[2]
    main_text = open(main_path, encoding="utf-8", newline="").read()

    if sys.argv[3] == "--list":
        # The table repeats a dependency once per reference, so collapse it and
        # show the count - a name appearing twice is two Call sites, which is
        # exactly what you need to know before a blind replace.
        found = targets(main_text)
        if not found:
            print("  no subVI links in this bundle")
        seen = {}
        for name in found:
            seen[name] = seen.get(name, 0) + 1
        for name, n in seen.items():
            print(f"  {name}" + (f"   ({n} references)" if n > 1 else ""))
        return

    if len(sys.argv) < 5:
        sys.exit("need both <old.vi> and <new.vi>")
    old, new = sys.argv[3], sys.argv[4]

    new_path = None
    if "--path" in sys.argv:
        target = sys.argv[sys.argv.index("--path") + 1]
        if not os.path.isabs(target):
            sys.exit("  ABORT: --path must be absolute - it becomes the link's stored path")
        if not os.path.exists(target):
            sys.exit(f"  ABORT: --path {target!r} does not exist. A link to a missing file makes "
                     "LabVIEW open a modal search dialog on load, which wedges the whole session.")
        if os.path.basename(target) != new:
            sys.exit(f"  ABORT: --path ends in {os.path.basename(target)!r} but the new subVI is "
                     f"{new!r}; the last path segment IS the file name.")
        drive, rest = os.path.splitdrive(os.path.abspath(target))
        new_path = [drive] + [p for p in rest.split(os.sep) if p]

    if old not in targets(main_text):
        sys.exit(f"  ABORT: {old!r} is not a subVI link in this bundle. "
                 f"Known: {', '.join(targets(main_text)) or 'none'}")

    # Replace ONLY inside the two link elements. A bare text replace is unsafe for
    # two measured reasons:
    #   1. SUBSTRING COLLISION. The VI's own name can contain the subVI's name -
    #      tl_cycle.vi contains cycle.vi - and it appears in the RSRC/LVSR header.
    #      A blind replace renamed the VI itself, destroying its identity.
    #   2. THE RECORD COUNT VARIES. A freshly retargeted bundle carried one link
    #      record (IUVI); after LabVIEW saved the VI there were two (VIVI as well),
    #      so any fixed expected count is wrong on one of them.
    qual = re.compile(r"(<LinkSaveQualName>\s*<String>)" + re.escape(old) + r"(</String>)")

    n_qual = len(qual.findall(main_text))
    if n_qual == 0:
        sys.exit(f"  ABORT: no <LinkSaveQualName> names {old!r}. Run --list.")

    main_text, n_path = retarget_paths(main_text, old, new, new_path)
    if n_path == 0:
        sys.exit(f"  ABORT: {n_qual} qualified name(s) for {old!r} but not one path ends in it. "
                 "Either the name is only a caption here, or the path is stored in a shape this "
                 "script does not recognise - inspect a <LinkSavePathRef> by hand before forcing it.")
    # n_path < n_qual is NORMAL and must not abort. Some link records store no path at all -
    # `<LinkSavePathRef Ident="PTH0" TpVal="0" ZeroFill="True" />`, self-closing - and one bundle
    # carried three qualified names against two real paths for the same subVI. The old guard
    # demanded equality and rejected that VI outright.
    if n_path < n_qual:
        print(f"  note              {n_qual - n_path} record(s) store no path (ZeroFill) - normal")

    main_text = qual.sub(lambda m: m.group(1) + new + m.group(2), main_text)
    open(main_path, "w", encoding="utf-8", newline="").write(main_text)
    where = "/".join(new_path) if new_path else "same folder"
    print(f"  link records      {n_qual} ({n_qual} name + {n_path} path)  {old} -> {new}  [{where}]")

    heap_text = open(heap_path, encoding="utf-8", newline="").read()
    caption_hits = heap_text.count(f'"{old}"')
    if caption_hits:
        open(heap_path, "w", encoding="utf-8", newline="").write(
            heap_text.replace(f'"{old}"', f'"{new}"'))
    print(f"  node caption      {caption_hits} replacement(s)")
    print("  now pylv_rebuild, then AIXML-export the result and read `target=`.")


main()
