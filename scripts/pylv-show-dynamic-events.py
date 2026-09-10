# -*- coding: utf-8 -*-
"""Show an Event Structure's DYNAMIC EVENT TERMINALS - in a pylabview bundle,
no LabVIEW running.

WHY THIS IS DONE UNCONDITIONALLY. The terminals are where a `Register For
Events` refnum is wired, and they are HIDDEN by default, so a generated VI has
no connection point for one: a user event cannot be attached at all until
somebody right-clicks the structure in the IDE. An unwired terminal is inert -
NI's own templates ship them hidden but present - so showing them costs nothing
and removes an IDE gesture from the route. The user's suggestion of 2026-09-10,
and it is the same shape as the Timeout frame: enable the thing that is free
when unused, because its absence is what blocks the whole capability.

MEASURED 2026-09-10 as a clean A/B - one working VI copied, the terminals turned
on in the IDE, saved, nothing else touched. Frame count 4 on both sides and
EventSpec count 4 on both, so the single variable really was the toggle:

    term wrapping an eventDynDCO   0x800040  ->  0x000040     (bit 23 cleared)
    term wrapping the eventTimeOut 0x000040      0x000040     (never hidden)
    term wrapping a selTun         0x400040      0x400040     (a tunnel)
    eventStruct objFlags           0x054A80  ->  0x014280     (0x040800 cleared)

So **`0x800000` on a `term` is HIDDEN**, and after the toggle the dynamic
terminals carry exactly the timeout terminal's flags. That reading is what makes
this safe to apply blind rather than a bit copied from one sample.

The earlier reading of the same `objFlags` pair was UNATTRIBUTABLE, and is worth
remembering as the trap: the first attempt measured it on a BROKEN VI, where
LabVIEW's save pruned five unregistered frames at the same time - two changes,
one observation. The numbers matched this pair exactly and still proved nothing.

usage:
  pylv-show-dynamic-events.py <bundle> <base>
"""
import io, re, sys

HIDDEN = 0x800000          # on a term
STRUCT_HIDDEN = 0x040800   # on the eventStruct itself


def main(bundle, base):
    p = "%s/%s_BDHb.xml" % (bundle, base)
    s = io.open(p, encoding='utf-8').read()

    if 'class="eventStruct"' not in s:
        print("no Event Structure on this diagram - nothing to show")
        return

    es = s.index('class="eventStruct"')
    end = s.index('<diagramList', es)
    changed = []

    # 1. each dynamic-event terminal
    while True:
        m = re.compile(r'(class="term" uid="(\d+)">\s*<objFlags>)(\d+)(</objFlags>\s*'
                       r'<dco class="eventDynDCO")').search(s, es, end)
        if not m:
            break
        flags = int(m.group(3))
        if not flags & HIDDEN:
            # already shown; skip past it so the scan can continue
            es = m.end()
            continue
        s = s[:m.start(3)] + str(flags & ~HIDDEN) + s[m.end(3):]
        changed.append("term %s 0x%06X -> 0x%06X"
                       % (m.group(2), flags, flags & ~HIDDEN))
        # the string moved; restart the scan from the structure
        es = s.index('class="eventStruct"')
        end = s.index('<diagramList', es)

    # 2. the structure's own flags
    es = s.index('class="eventStruct"')
    m = re.compile(r'(class="eventStruct" uid="\d+">\s*<objFlags>)(\d+)(</objFlags>)').search(s, es)
    assert m, "eventStruct has no objFlags"
    flags = int(m.group(2))
    if flags & STRUCT_HIDDEN:
        s = s[:m.start(2)] + str(flags & ~STRUCT_HIDDEN) + s[m.end(2):]
        changed.append("eventStruct 0x%06X -> 0x%06X"
                       % (flags, flags & ~STRUCT_HIDDEN))

    if not changed:
        print("dynamic event terminals already shown - nothing to do")
        return

    io.open(p, 'w', encoding='utf-8', newline='').write(s)
    for line in changed:
        print("   " + line)
    print("dynamic event terminals shown (%d change(s))" % len(changed))


if __name__ == '__main__':
    main(*sys.argv[1:])
