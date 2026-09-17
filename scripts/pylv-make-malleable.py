#!/usr/bin/env python3
"""Mark an extracted VI bundle as a MALLEABLE VI (.vim).

`ConvertAIXMLToVI` will happily write a file whose name ends in `.vim`, and that file
is BROKEN - `execState 0`, eBad. NI's published not-supported list says so ("new
malleable VIs"), and the mechanism is that AIXML cannot write VI properties: LabVIEW
decides malleability from the file EXTENSION at load time and then requires the VI to
be configured for inlining. A generated VI is not.

This sets the four LVSR flags that a malleable VI needs. Each one was bisected
separately on 2026-09-17 against NI's own `Shuffle 1D Array.vim`; dropping any single
one puts the VI back to eBad:

    Execution2 @ShouldInline   = 1   inline this subVI into its callers
    Unknown    @InlineStg      = 2   inline setting
    Instrument @DebugCapable   = 0   an inlined VI may not be debuggable
    Execution  @SaveParallel   = 1

Two things deliberately NOT touched, because they were measured NOT to matter:
`BadNode`, which is a cached verdict LabVIEW recomputes, and the four undecoded
`InStBit4/18/23/30` flags that also differ from NI's - setting ONLY those leaves the
VI eBad, and a VI with none of them set runs fine.

Usage, as the middle step of the ordinary pylabview cycle:

    pylv_extract  <the .vi>            ->  bundle/
    python pylv-make-malleable.py bundle/Thing.xml --apply
    pylv_rebuild  bundle/Thing.xml     ->  <the .vim>

The source VI must be generated as an ordinary `.vi` first and must already be
executable - this changes flags only, never the diagram. Rebuild to a path LabVIEW
has never loaded, or it keeps serving its stale in-memory copy and the verification
confirms the VI you replaced.

See docs/malleable-vis.md for the measurements.
"""

import argparse
import re
import sys

# attribute -> value a malleable VI needs
FLAGS = {
    "ShouldInline": "1",
    "InlineStg": "2",
    "DebugCapable": "0",
    "SaveParallel": "1",
}


def read(path):
    # binary, so the file's own line endings survive untouched
    with open(path, "rb") as handle:
        return handle.read()


def current(blob, name):
    found = re.search(rb'\b' + name.encode() + rb'="([^"]*)"', blob)
    return found.group(1).decode() if found else None


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("mainXml", help="the bundle's main .xml, as pylv_extract reports it")
    parser.add_argument("--apply", action="store_true",
                        help="write the flags; without it the current values are only listed")
    args = parser.parse_args()

    blob = read(args.mainXml)

    missing = [name for name in FLAGS if current(blob, name) is None]
    if missing:
        print("not a VI bundle this can mark - no %s attribute in %s"
              % (", ".join(missing), args.mainXml), file=sys.stderr)
        return 2

    changed = []
    for name, wanted in FLAGS.items():
        have = current(blob, name)
        state = "ok" if have == wanted else "-> %s" % wanted
        print("  %-14s = %s   %s" % (name, have, state))
        if have != wanted:
            changed.append(name)
            blob = re.sub(rb'\b' + name.encode() + rb'="[^"]*"',
                          name.encode() + b'="' + wanted.encode() + b'"', blob)

    if not changed:
        print("already marked malleable; nothing to write")
        return 0

    if not args.apply:
        print("%d flag(s) would change - pass --apply to write them" % len(changed))
        return 0

    with open(args.mainXml, "wb") as handle:
        handle.write(blob)
    print("wrote %s: %s" % (args.mainXml, ", ".join(changed)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
