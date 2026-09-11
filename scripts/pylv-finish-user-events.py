# -*- coding: utf-8 -*-
"""Re-write a VI's USER-EVENT EventSpecs after the dynamic event terminal has
been wired - in a pylabview bundle, no LabVIEW running.

WHY THIS EXISTS, AND WHY IT CANNOT BE DONE EARLIER. LabVIEW NORMALISES a
user-event EventSpec when it LOADS the VI, against the dynamic-event wire
present in the FILE at that moment. The wire is the one thing AIXML cannot
express, so at generation time it does not exist - and LabVIEW therefore throws
the spec away on its next load: `type` 1000 -> 0, `dynIndex` 1 -> 2, the frame
label back to `<#2>: Unknown Event (0x0)`.

So the order is forced, measured 2026-09-11 end to end on two VIs:

    1. lvai_generate_vi_with_events   static frames done; user-event spec lost
    2. lvai_wire_dynamic_events       makes the wire and SAVES - that save is
                                      the load that discards step 1's spec
    3. THIS SCRIPT                    write it again, onto the wired file

Both VIs went eBad -> eIdle on exactly step 3, and `type 1000` / `dynIndex 1`
then survived every later LabVIEW save.

HOW A FRAME IS RECOGNISED: `source` == 1. That is the dynamic/user-event
source, it is what separates those frames from a static `Value Change`
(`source` 3), and - the part that makes it usable here - LabVIEW's
normalisation LEAVES IT ALONE. So `source 1` with `type` != 1000 is exactly
"a user-event frame that needs finishing", readable after the save.

WHERE THE NAME COMES FROM: `VCTP`'s own
`<TypeDesc Type="Refnum" RefType="UserEvent" Label="...">`. It is the user
event's type label, it survives the normalisation, and it is only needed for
the CACHED frame label - the executability comes from the spec row alone.

WHAT IT REFUSES RATHER THAN GUESSES: more than one user-event frame, or a
number of `UserEvent` types that does not match. `dynIndex` is the registration
ITEM's position and only the value 1 has been measured, so a structure fed
several user events needs a mapping nobody here has established. Refusing is
the point: a wrong `dynIndex` gives a VI that loads, compiles and fires the
wrong event.

usage:
  pylv-finish-user-events.py <bundle> <base>

Exit 0 also means "nothing to do" - no user-event frame, or already
finished. The LAST line is machine-readable and says which:
`RESULT: changed` or `RESULT: nothing-to-do`, so a caller knows whether a
rebuild is needed at all - and therefore whether it has to release the VI
from LabVIEW's memory, which is the expensive part.
Re-running is safe and is how a regenerated VI is brought back.
"""
import importlib.util
import io
import os
import re
import sys

DYNAMIC_SOURCE = '1'
RESOLVED_TYPE = '1000'


def set_event_spec():
    """`pylv-set-event-spec.py`'s main(), imported by PATH.

    The filename has dashes, so it is not importable as a module. Loading it by
    path is deliberate: the spec row and the cached label are written in ONE
    place, and duplicating that logic here is how the two would drift apart.
    """
    here = os.path.dirname(os.path.abspath(__file__))
    path = os.path.join(here, 'pylv-set-event-spec.py')
    spec = importlib.util.spec_from_file_location('pylv_set_event_spec', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.main


def user_event_names(bundle, base):
    """Every user event's type label, in VCTP order."""
    path = "%s/%s.xml" % (bundle, base)
    text = io.open(path, encoding='utf-8', errors='replace').read()
    return re.findall(
        r'<TypeDesc[^>]*RefType="UserEvent"[^>]*Label="([^"]*)"', text)


def dynamic_frames(bundle, base):
    """(diagramIdx, type) for every EventSpec whose source is dynamic."""
    path = "%s/%s_BDHb.xml" % (bundle, base)
    text = io.open(path, encoding='utf-8', errors='replace').read()
    start = text.find('<EventNodeEvents')
    end = text.find('</EventNodeEvents>')
    if start < 0 or end < 0:
        return []
    out = []
    for block in re.finditer(
            r'<SL__arrayElement class="EventSpec">(.*?)</SL__arrayElement>',
            text[start:end], re.S):
        field = dict(re.findall(r'<(\w+)>([-\d]+)</\1>', block.group(1)))
        if field.get('source') == DYNAMIC_SOURCE:
            out.append((field.get('diagramIdx'), field.get('type')))
    return out


def main(bundle, base):
    frames = dynamic_frames(bundle, base)
    if not frames:
        print("nothing to do: no user-event frame on this Event Structure")
        print("RESULT: nothing-to-do")
        return
    unfinished = [(idx, kind) for idx, kind in frames if kind != RESOLVED_TYPE]
    if not unfinished:
        print("nothing to do: %d user-event frame(s), all already finished"
              % len(frames))
        print("RESULT: nothing-to-do")
        return

    names = user_event_names(bundle, base)
    if len(unfinished) > 1 or len(names) != 1:
        raise SystemExit(
            "REFUSED: %d user-event frame(s) to finish and %d UserEvent type(s) "
            "(%s). Only ONE of each is measured: dynIndex is the registration "
            "item's position and only the value 1 has been measured, so which "
            "frame gets which index is not established. A wrong dynIndex gives "
            "a VI that loads, compiles and fires the WRONG event, so this "
            "refuses rather than guessing. Write the specs by hand with "
            "pylv-set-event-spec.py --user-event, and measure the mapping."
            % (len(unfinished), len(names), ', '.join(names) or 'none'))

    diagram_idx, was = unfinished[0]
    print("finishing frame %s (type %s) as user event %r"
          % (diagram_idx, was, names[0]))
    set_event_spec()(bundle, base, diagram_idx, '--user-event', names[0])
    print("RESULT: changed")


if __name__ == '__main__':
    main(*sys.argv[1:])
