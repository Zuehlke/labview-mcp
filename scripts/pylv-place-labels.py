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

IT AVOIDS THE OTHER OBJECTS, and until 2026-09-08 it did not. The first version anchored a comment
a fixed `--gap` from its node and staggered only against OTHER COMMENTS, so on a dense diagram it
put documentation on top of wired elements - measured on a CLD exam solution, where six of twelve
comments landed on constants, terminals and a Case structure border, and one was clipped by the
frame it sat in. Getting the obstacle set right took four wrong models; all four are recorded here
because not one of them announces itself:

1. A diagram's `zPlaneList` holds only SOME of its objects. Constants, front panel terminals and
   indicators appear there as bare uid references and are DEFINED under
   `nodeList -> sRN -> termList -> term -> ddo`, with bounds in diagram space. Collecting only
   `zPlaneList` children missed 8 of 18 objects on one loop diagram.
2. The `sRN` pseudo-node has a degenerate box - `(-43, -1309, -43, -1309)` on that VI. Treating it
   as an obstacle is harmless in itself, but it BLOCKS THE DESCENT to the definitions in (1).
3. A constant's own bounds already span its caption. An `fPTerm`'s do NOT: those are just the
   32x16 socket, and the terminal NAME is a nested label whose bounds are RELATIVE to the socket.
4. A comment has to fit inside the structure that clips it, or LabVIEW cuts the text off silently.

TUNNELS AND WIRES CANNOT BE AVOIDED. Every tunnel uid in `tunnelList` / `srDCOList` is a bare
reference and no element carrying that uid has a `<bounds>` anywhere in the heap - searched, none.
Wires have no geometry either. Tunnels sit on a structure's left and right border, so the only
defence is a horizontal margin off those two edges; a comment crossing a WIRE is accepted, which
is the convention the user of this repository asked for on 2026-09-08.

A LABEL BOX DOES NOT AUTO-GROW. Text too long for its box is silently clipped - "paced at 50 ms"
rendered as "paced at 50". A box that cannot hold its text is resized here; one that can is left
alone, because its size is the author's choice.

AND THE ESTIMATE DECIDING "CAN HOLD" WAS WRONG IN THE DAMAGING DIRECTION UNTIL 2026-09-08, so the
resize did not fire and this script clipped a comment while reporting success. `Below the lower
edge of the band the heater must switch on` rendered as `... the heater must` in a 54 x 88 box:
the model said four lines and LabVIEW wrapped five. Two causes, both one-sided - a per-character
width average cannot describe a proportional font, and `round` on the line budget granted a
fraction of a line that does not exist. A wrong verdict here is invisible in every check the
chain makes: it validates, rebuilds, exports and runs, and only the rendered diagram disagrees.
Hence the constants above are pessimistic and the budget is floored.

Placement maximises CLEARANCE rather than taking the first free slot. First-fit is what leaves a
comment touching the thing next to it, which reads as badly as overlapping it.

usage:
  pylv-place-labels.py <bundle|heap_BDHb.xml> --list
  pylv-place-labels.py <bundle|heap_BDHb.xml> --place 900:130,901:135,910:230 [--gap 20]

Then `pylv_rebuild`. A rebuild verifies nothing - render the diagram and look. `Print.VI To HTML`
(see `scripts/lvdoc_print.xml`) writes one PNG per diagram; its image directory MUST EXIST, or
LabVIEW answers Error 118 without creating it.
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

# Classes whose bounds are RELATIVE to the object they belong to.
CAPTION_CLASSES = {"label", "multiLabel", "numLabel", "selLabel"}

# Never an obstacle: `attachment` is a comment's own anchor dot, and `sRN` is a pseudo-node whose
# degenerate box would hide every constant and terminal defined beneath it.
NOT_AN_OBSTACLE = {"attachment", "sRN"}

PAD = 10                # clear space demanded on every side of a placed comment
SIDE_MARGIN = 26        # keep off a structure's left/right border, where its tunnels sit
END_MARGIN = 12         # keep off its top/bottom border
REACH = 420             # past this a comment stops reading as documentation OF that node
# DELIBERATELY PESSIMISTIC, and the fit test is the only reason. A per-character average cannot
# describe a proportional font: measured 2026-09-08 in ONE 88 px box, LabVIEW fitted the 16
# characters of "the state of the" on a line and refused the 15 of "Below the lower" - under 5.5
# px/char one way, over 5.87 the other. So this sits above the observed upper bound rather than
# on the average (the old 5.2 came from "Release the state queue", 23 chars in 128 px = 5.57).
# The error is one-sided on purpose: over-estimating costs a comment a wider box it did not need,
# under-estimating SILENTLY CLIPS its last words, which is what shipped.
PIXELS_PER_CHAR = 6.0
LINE_HEIGHT = 13        # measured: four lines rendered inside a 54 px box, so at most 13.5
FIT_WIDTH = 150         # width given to a multi-line comment that had to be resized


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
    values = tuple(int(v) for v in raw.strip("() ").split(","))   # top, left, bottom, right
    return values if len(values) == 4 else None


def solid(bounds):
    """A box with real extent. A degenerate one is bookkeeping, not something on screen."""
    return bounds is not None and bounds[2] > bounds[0] and bounds[3] > bounds[1]


def caption_of(element):
    return (element.findtext("textRec/text")
            or element.findtext("label/textRec/text") or "").strip('"')


def survey(text):
    """One record per diagram, breadth first.

    Each record is {'objects': [...], 'owner': rect|None}. An object is
    (uid, cls, bounds, caption, kind, is_free_label) with bounds in THAT diagram's space, and kind
    is 'comment', 'node' or 'other'. `owner` is the structure that clips the diagram, so a comment
    can be kept inside it; it is None for the top-level diagram, which grows instead.
    """
    root = ET.fromstring(text)
    top = root.find("root")
    diagrams, queue = [], [(top if top is not None else root, None)]

    while queue:
        diag, owner = queue.pop(0)
        objects, nested = [], []

        zplane = diag.find("zPlaneList")
        # A FREE label sits directly in the diagram; a control's own caption sits inside that
        # control and is class="label" too. Only the free ones may be moved.
        free_ids = {id(c) for c in list(zplane)} if zplane is not None else set()

        def record(element, bounds, free):
            uid, cls = element.get("uid"), element.get("class")
            if not uid:
                return
            if cls == "label":
                kind = "comment" if free else "other"
            elif is_node(element, cls):
                kind = "node"
            else:
                kind = "other"
            objects.append((int(uid), cls, bounds, caption_of(element), kind, free))

        def walk(element, origin, inside):
            for child in list(element):
                if child.get("class") == "diag":
                    nested.append((child, origin))
                    continue
                bounds, cls = parse_bounds(child), child.get("class")
                usable = solid(bounds)
                if id(child) in free_ids:
                    if usable:
                        record(child, bounds, True)
                        walk(child, bounds, True)       # only to reach nested diagrams
                    else:
                        walk(child, origin, inside)
                elif usable and cls not in NOT_AN_OBSTACLE and not inside:
                    record(child, bounds, False)
                    walk(child, bounds, True)
                elif inside and usable and cls in CAPTION_CLASSES and origin:
                    record(child, (origin[0] + bounds[0], origin[1] + bounds[1],
                                   origin[0] + bounds[2], origin[1] + bounds[3]), False)
                    walk(child, origin, True)
                else:
                    walk(child, origin, inside)

        walk(diag, None, False)
        diagrams.append({"objects": objects, "owner": owner})
        queue += nested

    return diagrams


def index_by_uid(diagrams):
    """uid -> (diagram index, record)."""
    found = {}
    for n, diagram in enumerate(diagrams):
        for record in diagram["objects"]:
            found.setdefault(record[0], (n, record))
    return found


def clip_note(bounds, caption):
    """`` when the caption fits its own box, a CLIPPED warning when it does not.

    WHY THE LISTING CARRIES THIS. A label box does not auto-grow, so a caption too long for it is
    cut off mid-word in silence - and LabVIEW's OWN generated box does it: measured 2026-09-08, it
    gave a 57-character caption a 54 x 88 box and rendered `... the heater must`. That is the one
    layout fault that survives when comments are left UNPLACED, and it was costing a render plus an
    image read per VI to find. The arithmetic is already here, so the answer is a text field rather
    than a picture: no LabVIEW, no PNG, and it cannot overlook what an eye can.

    A SINGLE-LINE BOX IS NEVER FLAGGED, and that is the correction to this check's first version,
    which fired on 3 of 3 comments in a real run and pushed the agent into placing two of them -
    the exact work the unplaced path exists to avoid. Measured the same day on a three-comment
    probe, rendered and read:

        15 x 174 box, 34 chars, renders on ONE line, complete   -> 5.12 px/char
        15 x 192 box, 36 chars, renders on ONE line, complete   -> 5.33 px/char
        54 x  70 box, 37 chars, renders on FOUR lines, complete -> correctly silent

    So LabVIEW derives a one-line box's WIDTH from the caption at about 5.1-5.3 px per character,
    while this file's PIXELS_PER_CHAR is a deliberately pessimistic 6.0 - which demands 15-20 %
    more room than LabVIEW needs and therefore condemns every box LabVIEW sized correctly. Raising
    the constant is not the fix: it is right for the RESIZE decision, where erring small clips
    text. The fix is structural. **A one-line-high box is LabVIEW stating that the caption fits on
    one line at that width**; a multi-line box is where it may have capped the width and then run
    out of height, which is what the morning's 54 x 88 clip was.

    The residual risk, stated rather than hidden: a one-line box that is genuinely too narrow would
    now pass unflagged. None has been observed, and the mechanism above says none should exist,
    because the width is derived from the text. Multi-line boxes keep the pessimistic estimate.
    """
    height, width = bounds[2] - bounds[0], bounds[3] - bounds[1]
    if not caption or height <= 0 or width <= 0:
        return ""
    if height <= LINE_HEIGHT + 3:
        return ""
    needed = wrapped_lines(caption, width)
    holds = max(1, int(float(height) / LINE_HEIGHT))
    if needed <= holds:
        return ""
    return ("  CLIPPED? needs ~%d lines of %d px in a %d px box (%d fit) - place it, or shorten it"
            % (needed, LINE_HEIGHT, height, holds))


def show(diagrams):
    clipped = 0
    for n, diagram in enumerate(diagrams):
        comments = [r for r in diagram["objects"] if r[4] == "comment" and r[3]]
        targets = [r for r in diagram["objects"] if r[4] == "node"]
        if not comments and not targets:
            continue
        print("--- diagram %d ---" % n)
        for uid, _cls, bounds, caption, _kind, _free in sorted(comments, key=lambda r: r[2][1]):
            note = clip_note(bounds, caption)
            if note:
                clipped += 1
            print("  comment  uid %-6d at (top %d, left %d)  %r%s"
                  % (uid, bounds[0], bounds[1], caption, note))
        for uid, cls, bounds, caption, _kind, _free in sorted(targets, key=lambda r: r[2][1]):
            print("  target   uid %-6d %-26s at (top %d, left %d)  %s"
                  % (uid, describe(cls), bounds[0], bounds[1], caption))

    if clipped:
        print("%d comment(s) may be CLIPPED by their own box. That is the one layout fault an "
              "unplaced comment can still have - placing them resizes the box, and so does "
              "shortening the text. The estimate errs towards warning: a proportional font cannot "
              "be measured by character count, so check the ones it names." % clipped)


SUBVI_CLASSES = {"iUse", "polyIUse"}


def side_for(cls, side):
    """Which side of the node the comment is TRIED on first.

    `auto` follows the convention the user of this repository asked for on 2026-08-24: a comment
    ABOUT A SUBVI CALL reads better BELOW the node, while a general description of what a stretch of
    diagram does belongs above it. The target itself decides, so no per-comment flag is needed.
    Since 2026-09-08 this is a preference rather than a verdict: when the preferred side has no
    clear room the other one is taken, because a readable position beats a conventional one.
    """
    if side != "auto":
        return side
    return "below" if cls in SUBVI_CLASSES else "above"


def wrapped_lines(caption, width):
    """Greedy word wrap, the way LabVIEW breaks a label."""
    per_line = max(1, int(width / PIXELS_PER_CHAR))
    lines, used = 1, 0
    for word in caption.split():
        extra = len(word) + (1 if used else 0)
        if used + extra <= per_line:
            used += extra
        else:
            lines, used = lines + 1, len(word)
    return lines


def fit_options(bounds, caption, cramped=False):
    """Box shapes that hold `caption`, in preference order. The author's box wins when it fits.

    THE LINE BUDGET IS FLOORED, NEVER ROUNDED, and that was half of a real defect. `round` let a
    54 px box claim four 13 px lines' worth of room and a fraction more; combined with an
    optimistic PIXELS_PER_CHAR it passed a comment needing FIVE lines as fitting, and LabVIEW then
    clipped it without a word. Measured 2026-09-08 on `Below the lower edge of the band the heater
    must switch on`, which rendered as `... the heater must` inside a 54 x 88 box. Both halves of
    the estimate now err towards resizing.

    SEVERAL SHAPES, NOT ONE, and that is the correction to the correction. Resizing to a single
    fixed width made the fix WORSE than the defect on a cramped diagram: measured the same day, the
    three comments inside that VI's For Loop were each widened to 150 px, whereupon one of them no
    longer had a clear position anywhere in the loop and was placed overlapping at -24 px. So the
    wide shape is offered first because it reads best, and a shape keeping the author's WIDTH and
    growing DOWNWARDS follows it - a structure too narrow for the first often has room for the
    second. Placement tries them in this order and takes the first that clears its neighbours,
    which is why this returns a list and not a box.

    `cramped` REVERSES that order, and it is set for any comment INSIDE A STRUCTURE. Space there
    is finite and shared, so a greedy first-come widening starves whatever is placed after it:
    measured 2026-09-08 in that same For Loop, the first two comments took 150 px each and the
    third then had nowhere to go, which is a worse outcome for the diagram than three narrow boxes
    that all fit. On the root diagram there is room below and to the right, so `wide` stays first.
    """
    height, width = bounds[2] - bounds[0], bounds[3] - bounds[1]
    if not caption:
        return [(height, width)]

    def holds(box_height, box_width):
        return wrapped_lines(caption, box_width) <= max(1, int(float(box_height) / LINE_HEIGHT))

    if holds(height, width):
        return [(height, width)]

    if wrapped_lines(caption, FIT_WIDTH) == 1:
        wide = (15, int(len(caption) * PIXELS_PER_CHAR) + 8)
    else:
        wide = ((wrapped_lines(caption, FIT_WIDTH) + 1) * LINE_HEIGHT, FIT_WIDTH)

    tall = ((wrapped_lines(caption, width) + 1) * LINE_HEIGHT, width)
    if tall == wide:
        return [wide]
    return [tall, wide] if cramped else [wide, tall]


def clearance(box, obstacles):
    """Smallest edge distance to any obstacle; negative where the two overlap."""
    worst = None
    for other in obstacles:
        dx = max(other[1] - box[3], box[1] - other[3])
        dy = max(other[0] - box[2], box[0] - other[2])
        gap = max(dx, dy) if (dx < 0 or dy < 0) else max(0, min(dx, dy))
        worst = gap if worst is None else min(worst, gap)
    return 999 if worst is None else worst


def place(text, diagrams, pairs, gap, side):
    """Move each comment into the most open spot within reach of its node."""
    located = index_by_uid(diagrams)
    moving = {label for label, _target in pairs}
    # Obstacles per diagram. A comment we are NOT moving still blocks the ones we are.
    obstacles = {}
    for n, diagram in enumerate(diagrams):
        obstacles[n] = [r[2] for r in diagram["objects"]
                        if r[4] != "comment" or r[0] not in moving]

    plan = []
    for label_uid, target_uid in pairs:
        if label_uid not in located:
            raise SystemExit("no object with uid %d in this diagram heap" % label_uid)
        if target_uid not in located:
            raise SystemExit("no object with uid %d in this diagram heap" % target_uid)
        label_diagram, label = located[label_uid]
        target_diagram, target = located[target_uid]
        if label[4] != "comment":
            raise SystemExit("uid %d is a %s, not a comment" % (label_uid, describe(label[1])))
        if target[4] != "node":
            raise SystemExit("uid %d is a %s - not something to anchor a comment to"
                             % (target_uid, describe(target[1])))
        if label_diagram != target_diagram:
            raise SystemExit(
                "uid %d and uid %d are in DIFFERENT diagrams. Bounds are relative to the diagram, "
                "so this pairing would put the comment somewhere unrelated. Run --list: a comment "
                "can only be anchored inside the structure it already lives in."
                % (label_uid, target_uid))

        owner = diagrams[label_diagram]["owner"]
        blocking = obstacles[label_diagram]
        shapes = fit_options(label[2], label[3], cramped=owner is not None)

        anchor = target[2]
        prefer = side_for(target[1], side)
        limit_bottom = (owner[2] - owner[0] - END_MARGIN) if owner else 10 ** 6
        limit_right = (owner[3] - owner[1] - SIDE_MARGIN) if owner else 10 ** 6
        centre_x, centre_y = (anchor[1] + anchor[3]) / 2.0, (anchor[0] + anchor[2]) / 2.0

        # SHAPE BY SHAPE, widest first, and the FIRST that clears its neighbours wins - a later
        # shape is not compared against an earlier one on score. Deliberate: the shapes are
        # already ordered by how well they read, so a narrower box is a concession made only when
        # the better-looking one cannot be placed at all.
        best, best_score, best_side = None, None, prefer
        for height, width in shapes:
            current = (label[2][0], label[2][1], label[2][0] + height, label[2][1] + width)
            candidates = [(current[0], current[1], prefer)]
            for offset in range(gap, gap + 220, 16):
                for step in range(-8, 9):
                    shift = step * width // 4
                    candidates.append((anchor[0] - offset - height, anchor[1] + shift, "above"))
                    candidates.append((anchor[2] + offset, anchor[1] + shift, "below"))

            for top, left, where in candidates:
                box = (top, left, top + height, left + width)
                if (box[0] < END_MARGIN or box[1] < SIDE_MARGIN
                        or box[2] > limit_bottom or box[3] > limit_right):
                    continue
                room = clearance(box, blocking)
                if room < PAD:
                    continue
                away = (((left + width / 2.0) - centre_x) ** 2
                        + ((top + height / 2.0) - centre_y) ** 2) ** 0.5
                if away > REACH:
                    continue
                score = min(room, 60) - 0.12 * away + (6 if where == prefer else 0)
                if best_score is None or score > best_score:
                    best, best_score, best_side = box, score, where

            if best is not None:
                break

        if best is None:
            # THE NARROWEST shape, not the best-looking one: nothing cleared PAD, so what matters
            # now is staying inside the structure that clips this comment rather than reading well.
            height, width = shapes[-1]
            # Fall back to the old fixed-offset behaviour rather than refuse, and SAY SO - that
            # result needs a human eye on it.
            top = anchor[2] + gap if prefer == "below" else anchor[0] - gap - height
            left = anchor[1]
            # AND CLAMP IT INTO THE DIAGRAM. The scored path discards any box crossing a margin,
            # so only this branch can put a comment off-screen - and it did: measured 2026-09-08,
            # two comments above shallow anchors inside a For Loop landed at top -41 and -45,
            # invisible. An overlapping comment a reader can SEE and move is strictly better than
            # a correctly-sized one they cannot find, and the same run's own WARNING says which
            # ones to look at.
            top = max(END_MARGIN, min(top, limit_bottom - height))
            left = max(SIDE_MARGIN, min(left, limit_right - width))
            best = (top, left, top + height, left + width)
            print("  WARNING uid %d: no position with %d px clearance within %d px of its node - "
                  "placed %s it anyway, so LOOK at this one" % (label_uid, PAD, REACH, prefer))

        blocking.append(best)
        plan.append((label_uid, best, label[2], label[3], best_side,
                     clearance(best, [b for b in blocking if b is not best])))

    for label_uid, new_bounds, old_bounds, caption, where, room in plan:
        # Anchored replacement: a label's FIRST <bounds> after its opening tag is its own.
        anchor_match = re.search(r'<SL__arrayElement class="label" uid="%d">' % label_uid, text)
        if not anchor_match:
            raise SystemExit("uid %d is not a label element in the text" % label_uid)
        head, tail = text[:anchor_match.end()], text[anchor_match.end():]
        tail, replaced = re.subn(r"<bounds>\([^)]*\)</bounds>",
                                 "<bounds>(%d, %d, %d, %d)</bounds>" % new_bounds, tail, count=1)
        if replaced != 1:
            raise SystemExit("uid %d has no <bounds> to rewrite" % label_uid)
        text = head + tail
        resized = ((new_bounds[2] - new_bounds[0], new_bounds[3] - new_bounds[1])
                   != (old_bounds[2] - old_bounds[0], old_bounds[3] - old_bounds[1]))
        print("  %-5s %-28r (%d, %d) -> (%d, %d)  clearance %d px%s"
              % (where, caption, old_bounds[0], old_bounds[1], new_bounds[0], new_bounds[1],
                 room, "  resized to fit its text" if resized else ""))

    return text


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("bundle")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--place", metavar="labelUid:targetUid,...")
    ap.add_argument("--gap", type=int, default=20,
                    help="smallest distance between the comment and the node (default 20). A "
                         "comment may end up further away when nearer positions are occupied.")
    ap.add_argument("--side", choices=("auto", "above", "below"), default="auto",
                    help="auto (default): a comment on a subVI call goes BELOW it, everything "
                         "else above. A preference, not a guarantee - the other side is used "
                         "when the preferred one has no clear room.")
    args = ap.parse_args(argv)

    path = heap_path(args.bundle)
    text = read(path)
    diagrams = survey(text)

    if args.list or not args.place:
        show(diagrams)
        return 0

    pairs = []
    for pair in args.place.split(","):
        label_uid, target_uid = pair.split(":")
        pairs.append((int(label_uid), int(target_uid)))

    write(path, place(text, diagrams, pairs, args.gap, args.side))
    print("placed %d comment(s). Now pylv_rebuild - and then LOOK at the diagram; clearance is "
          "measured here, but whether a comment READS well is not." % len(pairs))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
