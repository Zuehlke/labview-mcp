"""Point a VI's subVI Call at a different subVI, in a pylabview-extracted bundle.

WHY THIS EXISTS - the slot pattern. AIXML cannot author a Timed Loop or an Event
Structure at all (`Error 53, Unsupported node type`), and it cannot author a Call to
the project's own code either (`Error 53, Unsupported SubVI`). pylabview can edit an
object heap but cannot compose one: no new nodes, no new wires. So a Timed Loop's
contents, and a generated unit test's call to its own subject, both looked unreachable.

They are not, once there is ONE subVI Call to swap. That Call is a socket. AIXML
authors the plug - a subVI, with no restriction on what is inside it, and it may live
in the project - and this script swaps which plug is in the socket. Measured
2026-08-22: a Timed Loop's Call retargeted from alternate.vi to alternate2.vi,
confirmed by LabVIEW's own AIXML export reading target="alternate2.vi", with the loop,
its Timeout/Period, the stop button and the indicator all untouched. Measured again
2026-08-27, this time onto project-local code: three Calls to a palette placeholder
became three Calls to `Celsius To Fahrenheit.vi`, and LabVIEW's export read them back
with the subject's own terminal names.

So the IDE is needed once per socket, never again for what goes in it.

WHERE THE NAME LIVES - per link record, plus a caption:
  main .xml   <LinkSaveQualName>   one <String> PER QUALIFIER SEGMENT, VI name last
  main .xml   <LinkSavePathRef>    one <String> PER PATH SEGMENT, file name last
  main .xml   <VILSPathRef>        the path of the OWNING LIBRARY, when there is one
  _BDHb.xml   <text>"NAME"</text>  the node's caption

THE QUALIFIED NAME IS A SEGMENT LIST, NOT A NAME, and reading only its first segment is
how this script used to mislead. A library-owned subVI stores

  <LinkSaveQualName><String>NI_Gmath.lvlib</String><String>Error Function.vi</String>

so `--list` printed "NI_Gmath.lvlib" for a bundle that calls `Error Function.vi` three
times, and passing the name off the diagram was rejected as "not a subVI link in this
bundle" - which reads as though the VI were not called at all. Names are now joined with
`:`, the way LabVIEW writes them, and `old` accepts either the joined name or just the
last segment when that is unambiguous.

THE PATH IS A SEGMENT LIST TOO. This mattered and cost a session's worth of hand-rolled
substitutions: a vi.lib dependency reads

  <LinkSavePathRef Ident="PTH0" TpVal="0">
    <String>&lt;vilib&gt;</String><String>Waveform</String>
    <String>WDTFileIO.llb</String><String>Export Waveforms To Spreadsheet File (1D).vi</String>

- symbolic root, folders, and only the LAST segment is the file name. Swapping a plug in
the SAME folder as the old one needs only that segment rewritten (the default). Pointing
a link at a VI somewhere else - the usual case when the new plug is your own code and the
old one was NI's - needs the whole segment list replaced, which is what --path is for.

THE OWNING LIBRARY HAS TO GO when the new target belongs to no library. A library-owned
record carries a third element the plain case does not:

  <VILSPathRef Ident="PTH0" TpVal="0">
    <String>&lt;vilib&gt;</String><String>gmath</String><String>NI_Gmath.lvlib</String>

Left in place it points at a library the new subVI is not in. It is replaced with the
empty ZeroFill form the bundle already uses for records that store no path. LabVIEW
loaded, relinked, ran and re-exported such a VI cleanly (2026-08-27) - which is one
working measurement, not a reference file, because no bundle was found that stores a
library-less call in a block pylabview parses.

EVERY EDIT IS SCOPED TO ONE LINK RECORD. Not tidiness: two subVIs of the SAME library -
Caraya's `Define Test.vi` and `Assert Equal Value_Variant.vi` in the measured bundle -
each carry their own VILSPathRef naming that library. A global replace would strip the
library from the record that was NOT retargeted and leave it dangling. Records are
delimited by their qualified name, which is the first element of each.

There can be MORE THAN ONE link record for the same subVI - a bundle carried one (IUVI)
right after a retarget and two (IUVI plus VIVI) once LabVIEW had saved the VI - so the
records are counted, never assumed. The caption is cosmetic, but a stale one makes the
diagram lie, so it is rewritten too.

WHY THE REPLACE IS ELEMENT-SCOPED AND NOT A TEXT REPLACE: the VI's own name can CONTAIN
the subVI's name. `tl_cycle.vi` contains `cycle.vi`, and the VI name sits in the
RSRC/LVSR header, so a bare replace renamed the VI itself. That is a silent identity
change, which is why matching is anchored to the link elements.

A POLYMORPHIC target needs TWO runs, and the tell is a caption count of 0. A call to a
polymorphic VI stores the WRAPPER's name in the diagram caption and the chosen INSTANCE
in its own link record, so retargeting the instance alone rewrites the link and leaves
the caption naming NI's wrapper - the diagram then lies about what it calls. Run the
script once per name, instance and wrapper, and check that between them the caption count
reaches 1. Measured while swapping `Export Waveforms To Spreadsheet File (1D).vi`
(instance, caption 0) and `Export Waveforms to Spreadsheet File.vi` (wrapper, caption 1).

THE ONE CONSTRAINT: the new subVI must keep the connector pane contract - same terminal
positions and types. The wires in the heap bind to the pane, so a different pane leaves
them dangling, and the symptom is not a link error: LabVIEW reports the CALLER as not
executable. Verify with lvai_connector_pane on both VIs before swapping, and always
AIXML-export the rebuilt VI afterwards to see what LabVIEW made of it.

usage: pylv-retarget-subvi.py <main.xml> <heap_BDHb.xml> <old.vi> <new.vi> [--path <C:\\dir\\new.vi>]
       pylv-retarget-subvi.py <main.xml> <heap_BDHb.xml> --list

  <old.vi>  the joined qualified name as --list prints it (`NI_Gmath.lvlib:Error
            Function.vi`), or just the last segment when only one link ends in it.
  <new.vi>  the new subVI's FILE NAME. A library-owned new target is refused rather
            than half-written - its VILSPathRef would have to name the library's path,
            which nothing here can derive.
"""
import os
import re
import sys


SEGMENT = re.compile(r"<String\s*/>|<String>([^<]*)</String>")
SEGMENT_LIST = r"((?:\s*<String\s*/>|\s*<String>[^<]*</String>)*)"
QUAL_BLOCK = re.compile(r"(<LinkSaveQualName>)" + SEGMENT_LIST + r"(\s*</LinkSaveQualName>)", re.S)
PATH_BLOCK = re.compile(r"(<LinkSavePathRef\b[^>]*>)" + SEGMENT_LIST +
                        r"(\s*</LinkSavePathRef>)", re.S)
VILS_BLOCK = re.compile(r"(<VILSPathRef\b[^>]*>)" + SEGMENT_LIST + r"(\s*</VILSPathRef>)", re.S)


def segments(body):
    """The segments AS THEY ARE STORED - still XML-escaped, empty <String/> as ''.

    Left escaped on purpose. A symbolic root is written `&lt;vilib&gt;`, and decoding it here
    only to re-encode it on the way out turned it into `&amp;lt;vilib&amp;gt;` - a segment
    literally named "&lt;vilib&gt;", which resolves to nothing. Segments that come from the
    FILE are copied through untouched; only a segment this script invents gets escaped.
    """
    return [(m.group(1) or "") for m in SEGMENT.finditer(body)]


def unescape(text):
    """For COMPARING a stored segment with something a caller typed. Never for writing back."""
    return text.replace("&lt;", "<").replace("&gt;", ">").replace("&amp;", "&")


def render_segments(parts, indent):
    # An empty segment goes back as `<String />`, the shape pylabview writes. Rendering it as
    # `<String></String>` is equivalent XML and the retarget worked either way, but it made every
    # untouched segment of a rewritten path show up in a diff - which is the wrong signal from a
    # tool whose whole safety argument is that you can see exactly what it changed.
    return "".join("\n%s<String />" % indent if p == "" else
                   "\n%s<String>%s</String>" % (indent, p) for p in parts)


def escape(text):
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def indent_of(body, fallback="            "):
    found = re.search(r"\n(\s*)<String", body)
    return found.group(1) if found else fallback


def qualified_names(main_text):
    """Every subVI link's qualified name, joined with ':', in document order."""
    return [":".join(unescape(s) for s in segments(m.group(2)))
            for m in QUAL_BLOCK.finditer(main_text)]


def records(main_text):
    """Split the document at each qualified name: (prefix, [chunk, ...]).

    A link record opens with its <LinkSaveQualName>, so a chunk running from one qualified
    name to the next holds that record's path and owning-library elements and nothing from
    the record before it. That is what keeps an edit off a sibling record from the same
    library.
    """
    starts = [m.start() for m in QUAL_BLOCK.finditer(main_text)]
    if not starts:
        return main_text, []
    bounds = starts + [len(main_text)]
    return main_text[:starts[0]], [main_text[bounds[i]:bounds[i + 1]]
                                   for i in range(len(starts))]


def resolve(names, old):
    """Which qualified name `old` means - joined form, or an unambiguous last segment."""
    if old in names:
        return old
    tail = [n for n in names if n.rsplit(":", 1)[-1] == old]
    if len(tail) == 1:
        return tail[0]
    if len(tail) > 1 and len(set(tail)) == 1:
        return tail[0]
    if len(tail) > 1:
        sys.exit(f"  ABORT: {old!r} is ambiguous - it is the last segment of "
                 f"{', '.join(sorted(set(tail)))}. Pass the joined name.")
    sys.exit(f"  ABORT: {old!r} is not a subVI link in this bundle. "
             f"Known: {', '.join(sorted(set(names))) or 'none'}")


def rewrite_chunk(chunk, old_qual, new_qual, old_file, new_file, new_path):
    """Apply the three link edits inside ONE record. Returns (chunk, did_path, did_library)."""
    old_library = old_qual.split(":")[0] if ":" in old_qual else None

    def qual(m):
        return m.group(1) + render_segments([escape(p) for p in new_qual.split(":")],
                                            indent_of(m.group(2))) + m.group(3)

    chunk = QUAL_BLOCK.sub(qual, chunk, count=1)

    did_path = 0

    def path(m):
        nonlocal did_path
        parts = segments(m.group(2))
        if did_path or not parts or unescape(parts[-1]) != old_file:
            return m.group(0)
        did_path = 1
        # `parts` came from the file and is already escaped; new segments are not.
        parts = [escape(q) for q in new_path] if new_path else parts[:-1] + [escape(new_file)]
        return m.group(1) + render_segments(parts, indent_of(m.group(2))) + m.group(3)

    chunk = PATH_BLOCK.sub(path, chunk)

    did_library = 0
    if old_library:
        def library(m):
            nonlocal did_library
            parts = segments(m.group(2))
            if did_library or not parts or unescape(parts[-1]) != old_library:
                return m.group(0)
            did_library = 1
            # The new target belongs to no library. This is the empty form the bundle
            # already uses for a link record that stores no path.
            return '<VILSPathRef Ident="PTH0" TpVal="0" ZeroFill="True" />'

        chunk = VILS_BLOCK.sub(library, chunk)

    return chunk, did_path, did_library


def main():
    if len(sys.argv) < 4:
        sys.exit(__doc__.strip().rsplit("usage:", 1)[-1].strip())

    main_path, heap_path = sys.argv[1], sys.argv[2]
    main_text = open(main_path, encoding="utf-8", newline="").read()
    names = qualified_names(main_text)

    if sys.argv[3] == "--list":
        # The table repeats a dependency once per link record, so collapse it and show the
        # count - a name appearing twice is two records, which is what you need to know
        # before a blind replace.
        if not names:
            print("  no subVI links in this bundle")
        seen = {}
        for name in names:
            seen[name] = seen.get(name, 0) + 1
        for name, n in seen.items():
            print(f"  {name}" + (f"   ({n} references)" if n > 1 else ""))
        return

    if len(sys.argv) < 5:
        sys.exit("need both <old.vi> and <new.vi>")
    old, new = sys.argv[3], sys.argv[4]

    if ":" in new:
        sys.exit(f"  ABORT: {new!r} names a library-owned target. Only the file name is "
                 "accepted: the record's VILSPathRef would have to carry that library's own "
                 "path, and nothing here can derive it. Retarget onto a library-less VI, or "
                 "write the library path in by hand.")

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

    old_qual = resolve(names, old)
    old_file = old_qual.rsplit(":", 1)[-1]

    prefix, chunks = records(main_text)
    n_qual = n_path = n_library = 0
    out = [prefix]
    for chunk in chunks:
        this = ":".join(unescape(s) for s in segments(QUAL_BLOCK.search(chunk).group(2)))
        if this != old_qual:
            out.append(chunk)
            continue
        chunk, did_path, did_library = rewrite_chunk(
            chunk, old_qual, new, old_file, new, new_path)
        n_qual += 1
        n_path += did_path
        n_library += did_library
        out.append(chunk)

    if n_path == 0:
        sys.exit(f"  ABORT: {n_qual} link record(s) for {old_qual!r} but not one path ends in "
                 f"{old_file!r}. Either the name is only a caption here, or the path is stored "
                 "in a shape this script does not recognise - inspect a <LinkSavePathRef> by "
                 "hand before forcing it.")
    # n_path < n_qual is NORMAL and must not abort. Some link records store no path at all -
    # `<LinkSavePathRef Ident="PTH0" TpVal="0" ZeroFill="True" />`, self-closing - and one bundle
    # carried three qualified names against two real paths for the same subVI. An older guard
    # demanded equality and rejected that VI outright.
    if n_path < n_qual:
        print(f"  note              {n_qual - n_path} record(s) store no path (ZeroFill) - normal")

    open(main_path, "w", encoding="utf-8", newline="").write("".join(out))
    where = "/".join(new_path) if new_path else "same folder"
    print(f"  link records      {n_qual} ({n_qual} name + {n_path} path)  "
          f"{old_qual} -> {new}  [{where}]")
    if n_library:
        print(f"  owning library    {n_library} VILSPathRef cleared - the new target is in none")

    heap_text = open(heap_path, encoding="utf-8", newline="").read()
    caption_hits = heap_text.count(f'"{old_file}"')
    if caption_hits:
        open(heap_path, "w", encoding="utf-8", newline="").write(
            heap_text.replace(f'"{old_file}"', f'"{new}"'))
    print(f"  node caption      {caption_hits} replacement(s)")
    print("  now pylv_rebuild, then AIXML-export the result and read `target=`.")


main()
