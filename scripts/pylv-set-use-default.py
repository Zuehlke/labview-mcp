# -*- coding: utf-8 -*-
"""Set "Use Default if Unwired" on the Case Structure OUTPUT tunnels that one
edit CREATED, in a pylabview bundle - no LabVIEW running.

WHY THIS EXISTS. lvai_wire_dynamic_events wires the Event Structure's
right-hand Event Registration Refnum terminal to Unregister For Events with
Connect Wire, and LabVIEW makes the tunnels on the way out. When the Event
Structure sits in one frame of a Case Structure - the Actor Framework helper
loop, an error case around the event loop - the new OUTPUT tunnel on that case
is UNWIRED in every other frame, and an output tunnel with no value in a frame
breaks the VI. The fix in the IDE is the tunnel's "Use Default if Unwired".

WHY IT IS DONE HERE AND NOT IN THE HELPER. The property exists - the string is in
LabVIEW's resource\\lvserver.rsc - but the AIXML generator refuses it on a
Property Node ("Invalid property"), and {LV.Tunnel}'s generic SetProperty is
unmeasured. The heap flag is measured instead, on a run that patched it and then
ran the VI: a `selTun` whose objFlags carry 0x200000 is a use-default tunnel. On
a wizard-generated accessor a use-default output selTun reads 0x300801 and a plain one
0x801; LabVIEW itself sets 0x100000 on a tunnel left unwired in a frame. This
script adds 0x200000 and nothing else.

WHICH TUNNELS. A candidate must be ALL of:
  - a `selTun` dco whose owner is a Case Structure (heap class `select`) - an
    Event Structure's tunnels are `selTun` too and are left alone;
  - an OUTPUT tunnel, (objFlags & 0xFFF) == 0x801 (an input reads 0x800);
  - without 0x200000 already;
  - with at least one INNER terminal - every termList uid but the last, one per
    frame - whose own objFlags lack the WIRED bit 0x8000;
  - NEW: its uid is absent from the --before bundle, an extract of the same VI
    taken BEFORE the edit. A tunnel the user already had is never touched, even
    one left broken the same way, because setting use-default on it changes what
    the user's code does.

THE BASELINE IS A GUARD AS WELL AS A FILTER. "New" means "uid not in the
baseline", which is only sound if LabVIEW kept the uids of everything that
existed. Observed on one VI: generated structure uids survived LabVIEW's load, a
Connect Wire and a save, while the tunnels Connect Wire created got low
LabVIEW-assigned ones. That is one VI, not a rule - so EVERY selTun uid of the
baseline must still exist after the edit, which a wire addition always
satisfies. If any is missing the uids are not comparable and this REFUSES,
patching nothing.

usage:
  pylv-set-use-default.py <bundle> <base> --before <before-bundle> <before-base> [--check]

The LAST line is machine-readable: `RESULT: changed`, `RESULT: nothing-to-do`,
`RESULT: would-change` (with --check) or `RESULT: refused`. The line before it,
`USE_DEFAULT_SET: <uid>,<uid>` (or `USE_DEFAULT_CANDIDATES:` with --check),
names the tunnels. A refusal exits 0 on purpose: it is a verdict about this
edit, not a failure of the script, and the caller decides what it costs.
Exit 1 is a real failure - a missing file, a heap that does not parse.
Re-running is safe: a tunnel already carrying 0x200000 is not a candidate.
"""
import io
import re
import sys
import xml.etree.ElementTree as ET

WIRED = 0x8000
USE_DEFAULT = 0x200000
KIND_MASK = 0xFFF
OUTPUT_TUNNEL = 0x801
CASE_STRUCTURE = 'select'


def bdhb(bundle, base):
    return "%s/%s_BDHb.xml" % (bundle, base)


def read(path):
    # newline='' keeps the LF endings: text mode would rewrite every line and
    # hide the one that changed.
    with io.open(path, encoding='utf-8', newline='') as f:
        return f.read()


def int_text(element, tag):
    child = element.find(tag)
    if child is None or child.text is None:
        return 0
    try:
        return int(child.text.strip())
    except ValueError:
        return 0


def analyse(text):
    """(term flags by uid, selTun records) for one block-diagram heap."""
    root = ET.fromstring(text.encode('utf-8'))
    parent = {child: node for node in root.iter() for child in node}

    terms = {}
    for element in root.iter('SL__arrayElement'):
        if element.get('class') == 'term' and element.find('objFlags') is not None:
            terms[element.get('uid')] = int_text(element, 'objFlags')

    tunnels = []
    for dco in root.iter('dco'):
        if dco.get('class') != 'selTun':
            continue
        owner = dco
        while owner in parent:
            owner = parent[owner]
            if owner.get('class') not in (None, 'term'):
                break
        term_list = dco.find('termList')
        uids = [] if term_list is None else [e.get('uid') for e in term_list]
        tunnels.append({
            'uid': dco.get('uid'),
            'flags': int_text(dco, 'objFlags'),
            'owner': owner.get('class'),
            'ownerUid': owner.get('uid'),
            'inner': uids[:-1],
            'outer': uids[-1] if uids else None,
        })
    return terms, tunnels


def candidates(terms, tunnels, baseline_uids):
    out = []
    for tunnel in tunnels:
        flags = tunnel['flags']
        if tunnel['owner'] != CASE_STRUCTURE:
            continue
        if (flags & KIND_MASK) != OUTPUT_TUNNEL:
            continue
        if flags & USE_DEFAULT:
            continue
        if tunnel['uid'] in baseline_uids:
            continue
        unwired = [u for u in tunnel['inner'] if not (terms.get(u, 0) & WIRED)]
        if unwired:
            out.append((tunnel, unwired))
    return out


def patch(text, uid, new_flags):
    pattern = re.compile(r'(<dco class="selTun" uid="%s">\s*<objFlags>)(\d+)(</objFlags>)'
                         % re.escape(uid))
    result, n = pattern.subn(lambda m: m.group(1) + str(new_flags) + m.group(3), text, count=1)
    if n != 1:
        raise SystemExit("could not locate selTun %s's objFlags in the heap text" % uid)
    return result


def main(argv):
    check = '--check' in argv
    argv = [a for a in argv if a != '--check']
    if '--before' not in argv:
        raise SystemExit(
            "usage: pylv-set-use-default.py <bundle> <base> --before <before-bundle> "
            "<before-base> [--check]\n"
            "--before is required: without a baseline every broken case tunnel in the VI "
            "would look new, including ones the user made.")
    at = argv.index('--before')
    if at != 2 or len(argv) != 5:
        raise SystemExit("usage: pylv-set-use-default.py <bundle> <base> --before "
                         "<before-bundle> <before-base> [--check]")
    bundle, base, before_bundle, before_base = argv[0], argv[1], argv[3], argv[4]

    after_path = bdhb(bundle, base)
    after_text = read(after_path)
    terms, tunnels = analyse(after_text)
    _, before_tunnels = analyse(read(bdhb(before_bundle, before_base)))

    before_uids = set(t['uid'] for t in before_tunnels)
    after_uids = set(t['uid'] for t in tunnels)
    missing = sorted(before_uids - after_uids, key=lambda u: int(u) if u.isdigit() else 0)
    new = sorted(after_uids - before_uids, key=lambda u: int(u) if u.isdigit() else 0)
    print("selTun before: %d, after: %d, new: %s"
          % (len(before_uids), len(after_uids), ','.join(new) or 'none'))

    if missing:
        print("REFUSED: %d tunnel uid(s) of the baseline are gone after the edit (%s). "
              "Adding a wire removes no tunnel, so the uids are not comparable - LabVIEW "
              "renumbered, or the baseline is not the state that was edited (unsaved changes "
              "in memory?). Nothing was patched." % (len(missing), ','.join(missing)))
        print("USE_DEFAULT_SET: ")
        print("RESULT: refused")
        return

    found = candidates(terms, tunnels, before_uids)
    for tunnel, unwired in found:
        print("   case %s output selTun %s flags %s -> %s (unwired inner terminal(s) %s)"
              % (tunnel['ownerUid'], tunnel['uid'], hex(tunnel['flags']),
                 hex(tunnel['flags'] | USE_DEFAULT), ','.join(unwired)))

    uids = ','.join(t['uid'] for t, _ in found)
    if not found:
        print("nothing to do: no NEW case output tunnel is unwired in any frame")
        print("USE_DEFAULT_SET: ")
        print("RESULT: nothing-to-do")
        return

    if check:
        print("USE_DEFAULT_CANDIDATES: %s" % uids)
        print("RESULT: would-change")
        return

    text = after_text
    for tunnel, _ in found:
        text = patch(text, tunnel['uid'], tunnel['flags'] | USE_DEFAULT)
    with io.open(after_path, 'w', encoding='utf-8', newline='') as f:
        f.write(text)
    print("USE_DEFAULT_SET: %s" % uids)
    print("RESULT: changed")


if __name__ == '__main__':
    main(sys.argv[1:])
