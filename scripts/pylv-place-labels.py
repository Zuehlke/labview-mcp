"""Put a block-diagram comment where it belongs, in a pylabview-extracted bundle.

WHY THIS EXISTS. AIXML can CREATE a diagram comment - `<FreeLabel comment="..."/>` - and cannot
place one: "NO LAYOUT. There is no coordinate attribute anywhere" (`docs/aixml-reference.md` §1).
The generator picks the position, and what it picks is not the node you meant. Measured on
`DaqReadAndTDMS2.vi`, 2026-08-24: six comments authored in one block ahead of the first `Call`
came out at six plausible node positions with the text-to-node mapping shifted - "Task stoppen und
freigeben" landed correctly over `DAQmx Stop Task` at x=1227, while "TDMS-Logging einschalten"
landed over the CSV subVI at x=1423 and "Timing 100 Hz" ended up in the top-left corner over a
wire. A comment on the wrong node is worse than no comment, because it is read as documentation.

Position, unlike a comment's existence, is just numbers in the object heap - so it is exactly what
pylabview is for. No nodes are added and no wires are drawn; one `<bounds>` per label is rewritten.

THE TRAP THIS GUARDS. Bounds are relative to the DIAGRAM the object sits in, not to the VI. A node
inside a For Loop is at (287, 76) in the loop's own space while the loop is at (77, 721) in the
root's - so placing a root-level label "above" a node inside a loop by copying its numbers puts the
label somewhere else entirely, off-screen as often as not. `--place` therefore refuses a pair whose
label and target do not share a diagram, and `--list` groups by diagram so the pairs are pickable.

usage:
  pylv-place-labels.py <bundle|heap_BDHb.xml> --list
  pylv-place-labels.py <bundle|heap_BDHb.xml> --place 900:130,901:135,910:230 [--gap 20]

Then `pylv_rebuild`. A rebuild verifies nothing - open the VI and look, or print the diagram.
"""

import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET

# Names for the classes worth pointing a comment at. This is a LOOKUP, not the test - LabVIEW has a
# class per primitive family and enumerating them is a losing game: `Concatenate Strings` is
# `concat`, not `prim`, and a list that missed it silently offered no way to comment the one node
# the comment was about. The test is `is_node()` below.
CLASS_NAMES = {
    "iUse": "subVI call",
    "polyIUse": "subVI call (polymorphic)",
    "prim": "primitive",
    "forLoop": "For Loop",
    "whileLoop": "While Loop",
    "select": "Case structure",
    "eventStruct": "Event structure",
    "seqStruct": "Sequence structure",
}

# Structures own a diagram rather than a terminal list, so they are named explicitly.
STRUCTURES = {"forLoop", "whileLoop", "select", "eventStruct", "seqStruct", "timedLoop"}


def is_node(element, cls):
    """Anything a wire can reach: a node has a `termList`, a structure owns a diagram.

    Decorations, free labels and the attachment points of wires have neither, and anchoring a
    comment to one of those would read as documentation of nothing.
    """
    return cls in STRUCTURES or element.find("termList") is not None


def describe(cls):
    return CLASS_NAMES.get(cls, cls)


def heap_path(argument):
    if os.path.isdir(argument):
        found = [n for n in os.listdir(argument) if n.endswith("_BDHb.xml")]
        if len(found) != 1:
            raise SystemExit("cannot tell which is the diagram heap in %s: %s" % (argument, found))
        return os.path.join(argument, found[0])
    return argument


def read(path):
    with open(path, encoding="utf-8", newline="") as f:
        return f.read()


def write(path, text):
    # newline="" keeps pylabview's LF endings; text mode would rewrite every line as CRLF and hide
    # the one that changed.
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)


def parse_bounds(element):
    raw = element.findtext("bounds")
    if not raw:
        return None
    return tuple(int(v) for v in raw.strip("() ").split(","))   # top, left, bottom, right


def survey(text):
    """(objects, diagram_of) - every uid'd object with bounds, and which diagram it lives in.

    Diagram identity is the enclosing `zPlaneList` ELEMENT, compared by identity rather than by
    name: a VI has one per structure frame and they are all called the same thing.
    """
    root = ET.fromstring(text)
    parent = {child: par for par in root.iter() for child in par}

    def diagram_of(element):
        cur = parent.get(element)
        while cur is not None and cur.tag != "zPlaneList":
            cur = parent.get(cur)
        return id(cur)

    objects = {}
    for element in root.iter("SL__arrayElement"):
        uid, cls = element.get("uid"), element.get("class")
        if not uid or not cls or element.find("bounds") is None:
            continue
        bounds = parse_bounds(element)
        if bounds is None:
            continue
        # A FREE label sits directly in the diagram; a control's own caption sits in that control's
        # `partsList` and is class="label" too. Without this the listing offers "status", "code" and
        # every terminal name as things to move, and moving one detaches a caption from its control.
        if cls == "label" and parent.get(element) is not None \
                and parent[element].tag != "zPlaneList":
            continue
        caption = (element.findtext("textRec/text")
                   or element.findtext("label/textRec/text") or "").strip('"')
        kind = "label" if cls == "label" else ("node" if is_node(element, cls) else "other")
        objects[int(uid)] = (cls, bounds, caption, diagram_of(element), kind)
    return objects


def show(objects):
    diagrams = {}
    for uid, (cls, bounds, caption, diagram, kind) in objects.items():
        diagrams.setdefault(diagram, []).append((uid, cls, bounds, caption, kind))

    for n, items in enumerate(diagrams.values()):
        labels = [i for i in items if i[4] == "label" and i[3]]
        targets = [i for i in items if i[4] == "node"]
        if not labels and not targets:
            continue
        print("--- diagram %d ---" % n)
        for uid, _, bounds, caption, _kind in sorted(labels, key=lambda i: i[2][1]):
            print("  comment  uid %-6d at (top %d, left %d)  %r" % (uid, bounds[0], bounds[1], caption))
        for uid, cls, bounds, caption, _kind in sorted(targets, key=lambda i: i[2][1]):
            print("  target   uid %-6d %-26s at (top %d, left %d)  %s"
                  % (uid, describe(cls), bounds[0], bounds[1], caption))


SUBVI_CLASSES = {"iUse", "polyIUse"}


def side_for(cls, side):
    """Which side of the node the comment goes on.

    `auto` follows the convention the user of this repository asked for on 2026-08-24: a comment
    ABOUT A SUBVI CALL reads better BELOW the node, while a general description of what a stretch of
    diagram does belongs above it. The target itself decides, so no per-comment flag is needed - a
    comment anchored to a subVI goes below, one anchored to a primitive or a structure goes above.
    """
    if side != "auto":
        return side
    return "below" if cls in SUBVI_CLASSES else "above"


def place(text, objects, pairs, gap, side):
    """Move each comment clear of its target, staggering away where two would overlap."""
    rows = {}          # (diagram, side, row) -> list of (left, right) already taken
    plan = []

    # Left to right, so a stagger decision only ever looks at comments already placed.
    for label_uid, target_uid in sorted(pairs, key=lambda p: objects[p[1]][1][1]):
        label = objects.get(label_uid)
        target = objects.get(target_uid)
        if label is None:
            raise SystemExit("no object with uid %d in this diagram heap" % label_uid)
        if target is None:
            raise SystemExit("no object with uid %d in this diagram heap" % target_uid)
        if label[4] != "label":
            raise SystemExit("uid %d is a %s, not a comment" % (label_uid, label[0]))
        if target[4] != "node":
            raise SystemExit("uid %d is a %s - not something to anchor a comment to"
                             % (target_uid, describe(target[0])))
        if label[3] != target[3]:
            raise SystemExit(
                "uid %d and uid %d are in DIFFERENT diagrams. Bounds are relative to the diagram, "
                "so this pairing would put the comment somewhere unrelated. Run --list: a comment "
                "can only be anchored inside the structure it already lives in." % (label_uid, target_uid))

        top, left, bottom, right = label[1]
        height, width = bottom - top, right - left
        anchor_top, anchor_left, anchor_bottom = target[1][0], target[1][1], target[1][2]
        where = side_for(target[0], side)

        # Stagger AWAY from the node, so a displaced comment never crosses it.
        row, step = 0, height + 4
        while True:
            taken = rows.setdefault((label[3], where, row), [])
            if all(anchor_left >= r or anchor_left + width <= l for l, r in taken):
                taken.append((anchor_left, anchor_left + width))
                break
            row += 1

        if where == "below":
            new_top = anchor_bottom + gap + row * step
        else:
            new_top = anchor_top - gap - height - row * step
        plan.append((label_uid, (new_top, anchor_left, new_top + height, anchor_left + width),
                     label[1], label[2], where))

    for label_uid, new_bounds, old_bounds, caption, where in plan:
        # Anchored replacement: a label's FIRST <bounds> after its opening tag is its own.
        anchor = re.search(r'<SL__arrayElement class="label" uid="%d">' % label_uid, text)
        if not anchor:
            raise SystemExit("uid %d is not a label element in the text" % label_uid)
        head, tail = text[:anchor.end()], text[anchor.end():]
        tail, n = re.subn(r"<bounds>\([^)]*\)</bounds>",
                          "<bounds>(%d, %d, %d, %d)</bounds>" % new_bounds, tail, count=1)
        if n != 1:
            raise SystemExit("uid %d has no <bounds> to rewrite" % label_uid)
        text = head + tail
        print("  %-5s %-28r (%d, %d) -> (%d, %d)"
              % (where, caption, old_bounds[0], old_bounds[1], new_bounds[0], new_bounds[1]))

    return text


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("bundle")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--place", metavar="labelUid:targetUid,...")
    ap.add_argument("--gap", type=int, default=20,
                    help="pixels between the comment and the node (default 20)")
    ap.add_argument("--side", choices=("auto", "above", "below"), default="auto",
                    help="auto (default): a comment on a subVI call goes BELOW it, everything "
                         "else above. Override to force one side for every comment.")
    args = ap.parse_args(argv)

    path = heap_path(args.bundle)
    text = read(path)
    objects = survey(text)

    if args.list or not args.place:
        show(objects)
        return 0

    pairs = []
    for pair in args.place.split(","):
        label_uid, target_uid = pair.split(":")
        pairs.append((int(label_uid), int(target_uid)))

    write(path, place(text, objects, pairs, args.gap, args.side))
    print("placed %d comment(s). Now pylv_rebuild - and then LOOK at the diagram; nothing here "
          "can tell you a comment reads well." % len(pairs))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
