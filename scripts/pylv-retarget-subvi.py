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
  main .xml   <LinkSavePathRef ...><String/><String>NAME</String>   the path
  _BDHb.xml   <text>"NAME"</text>                          the node's caption

There can be MORE THAN ONE link record for the same subVI - a bundle carried one
(IUVI) right after a retarget and two (IUVI plus VIVI) once LabVIEW had saved the
VI - so the pairs are counted, never assumed. The caption is cosmetic, but a
stale one makes the diagram lie, so it is rewritten too.

WHY THE REPLACE IS ELEMENT-SCOPED AND NOT A TEXT REPLACE: the VI's own name can
CONTAIN the subVI's name. `tl_cycle.vi` contains `cycle.vi`, and the VI name sits
in the RSRC/LVSR header, so a bare replace renamed the VI itself. That is a
silent identity change, which is why matching is anchored to the link elements.

THE ONE CONSTRAINT: the new subVI must keep the connector pane contract - same
terminal names and types. The wires in the heap bind to the pane, so a different
pane leaves them dangling. Verify with lvai_connector_pane on both VIs before
swapping, and always AIXML-export the rebuilt VI afterwards to see what LabVIEW
made of it.

usage: pylv-retarget-subvi.py <main.xml> <heap_BDHb.xml> <old.vi> <new.vi>
       pylv-retarget-subvi.py <main.xml> <heap_BDHb.xml> --list
"""
import re
import sys



def targets(main_text):
    """Every subVI name the link table mentions, in document order."""
    return re.findall(r"<LinkSaveQualName>\s*<String>([^<]+)</String>", main_text)


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
    path = re.compile(r"(<LinkSavePathRef\b[^>]*>(?:\s*<String\s*/>)*\s*<String>)"
                      + re.escape(old) + r"(</String>)")

    n_qual = len(qual.findall(main_text))
    n_path = len(path.findall(main_text))
    if n_qual == 0:
        sys.exit(f"  ABORT: no <LinkSaveQualName> names {old!r}. Run --list.")
    if n_qual != n_path:
        sys.exit(f"  ABORT: {n_qual} qualified name(s) but {n_path} path(s) for {old!r}. "
                 "A link record is half-written - inspect it by hand.")

    main_text = path.sub(lambda m: m.group(1) + new + m.group(2),
                         qual.sub(lambda m: m.group(1) + new + m.group(2), main_text))
    open(main_path, "w", encoding="utf-8", newline="").write(main_text)
    print(f"  link records      {n_qual} ({n_qual} name + {n_path} path)  {old} -> {new}")

    heap_text = open(heap_path, encoding="utf-8", newline="").read()
    caption_hits = heap_text.count(f'"{old}"')
    if caption_hits:
        open(heap_path, "w", encoding="utf-8", newline="").write(
            heap_text.replace(f'"{old}"', f'"{new}"'))
    print(f"  node caption      {caption_hits} replacement(s)")
    print("  now pylv_rebuild, then AIXML-export the result and read `target=`.")


main()
