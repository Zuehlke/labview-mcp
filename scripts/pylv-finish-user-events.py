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

WHICH USER EVENT A FRAME HANDLES - the part LabVIEW's normalisation destroys.
With ONE user-event frame and ONE user event there is nothing to decide. With
several, the answer is in the file AS STEP 1 WROTE IT: `pylv-set-event-spec.py`
gives each frame its event's `dynIndex` - the event's 1-based position among the
registration's items, measured 2026-10-10 on MGI's Actor Core.vi and the icon
editor's Buffer for lossless tracking.vi - and the registration in the same file
maps the position back to the name. So the caller extracts the VI BEFORE the
wiring save and passes that bundle as `--before`; this reads each frame's
event there and writes it again here, the position resolved afresh against the
wired file's own registration. Until 2026-10-10 this script REFUSED more than
one frame, because that mapping was not measured.

WHAT IT STILL REFUSES RATHER THAN GUESSES: several frames with no `--before`,
or a `--before` whose frames do not say which event they handle (a VI
generated before 2026-10-10 wrote `dynIndex 1` on every frame). A wrong
`dynIndex` gives a VI that loads, compiles and fires the WRONG event.

usage:
  pylv-finish-user-events.py <bundle> <base> [--before <bundle> <base>]

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


def spec_module():
    """`pylv-set-event-spec.py`, imported by PATH.

    The filename has dashes, so it is not importable as a module. Loading it by
    path is deliberate: the spec row, the cached label and the registration
    reading live in ONE place, and duplicating them here is how they would
    drift apart.
    """
    here = os.path.dirname(os.path.abspath(__file__))
    path = os.path.join(here, 'pylv-set-event-spec.py')
    spec = importlib.util.spec_from_file_location('pylv_set_event_spec', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def user_event_names(bundle, base):
    """Every user event's type label, in VCTP order - the fallback when the
    bundle carries no readable registration."""
    path = "%s/%s.xml" % (bundle, base)
    text = io.open(path, encoding='utf-8', errors='replace').read()
    return re.findall(
        r'<TypeDesc[^>]*RefType="UserEvent"[^>]*Label="([^"]*)"', text)


def dynamic_frames(bundle, base):
    """(diagramIdx, type, dynIndex) for every EventSpec whose source is dynamic."""
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
            out.append((field.get('diagramIdx'), field.get('type'), field.get('dynIndex')))
    return out


def frames_before(module, bundle, base):
    """{diagramIdx: user event name} off the bundle AS GENERATED - each frame's
    dynIndex mapped back through that bundle's own registration. Refuses what
    does not identify a frame."""
    items = module.registration_items(bundle, base)
    if items is None:
        raise SystemExit("REFUSED: the --before bundle carries no event registration, so its "
                         "frames' dynIndex cannot be mapped to user events.")
    out = {}
    for idx, kind, dyn in dynamic_frames(bundle, base):
        if kind != RESOLVED_TYPE:
            continue
        pos = int(dyn or 0)
        if not 1 <= pos <= len(items) or items[pos - 1] is None:
            raise SystemExit(
                "REFUSED: frame %s of the --before bundle names registration item %s, which is "
                "not a user event (the registration holds %s)."
                % (idx, dyn, ", ".join(n or "(not a user event)" for n in items)))
        out[idx] = items[pos - 1]
    names = list(out.values())
    if len(set(names)) != len(names):
        raise SystemExit(
            "REFUSED: in the --before bundle several frames name the same user event (%s) - "
            "a VI generated before 2026-10-10 wrote dynIndex 1 on every frame, so its frames "
            "do not say which event they handle. Generate it again with "
            "lvai_generate_vi_with_events, which writes each frame's real position."
            % ", ".join("frame %s: %s" % kv for kv in sorted(out.items())))
    return out


def main(*argv):
    bundle, base = argv[0], argv[1]
    before = None
    if '--before' in argv:
        i = argv.index('--before')
        before = (argv[i + 1], argv[i + 2])

    frames = dynamic_frames(bundle, base)
    if not frames:
        print("nothing to do: no user-event frame on this Event Structure")
        print("RESULT: nothing-to-do")
        return
    unfinished = [idx for idx, kind, _ in frames if kind != RESOLVED_TYPE]
    if not unfinished:
        print("nothing to do: %d user-event frame(s), all already finished"
              % len(frames))
        print("RESULT: nothing-to-do")
        return

    module = spec_module()
    items = module.registration_items(bundle, base)
    names = [n for n in items if n is not None] if items is not None else user_event_names(bundle, base)

    if len(unfinished) == 1 and len(names) == 1:
        plan = {unfinished[0]: names[0]}
    elif before is not None:
        known = frames_before(module, *before)
        missing = [idx for idx in unfinished if idx not in known]
        if missing:
            raise SystemExit(
                "REFUSED: frame(s) %s need finishing and the --before bundle does not say which "
                "user event they handle (it identifies frames %s)."
                % (", ".join(missing), ", ".join(sorted(known)) or "none"))
        plan = {idx: known[idx] for idx in unfinished}
    else:
        raise SystemExit(
            "REFUSED: %d user-event frame(s) to finish and %d user event(s) (%s), and no "
            "--before bundle to say which frame handles which. dynIndex is the event's position "
            "in the registration, and after the wiring save no frame names its event any more - "
            "a wrong dynIndex gives a VI that loads, compiles and fires the WRONG event, so this "
            "refuses rather than guessing. Pass the VI as generated, extracted BEFORE the wiring, "
            "as --before <bundle> <base>."
            % (len(unfinished), len(names), ', '.join(names) or 'none'))

    for idx in sorted(plan, key=int):
        print("finishing frame %s as user event %r" % (idx, plan[idx]))
        module.main(bundle, base, idx, '--user-event', plan[idx])
    print("RESULT: changed")


if __name__ == '__main__':
    main(*sys.argv[1:])
