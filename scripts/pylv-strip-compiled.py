# -*- coding: utf-8 -*-
"""Strip a VI's COMPILED CODE out of a pylabview bundle and mark it source-only,
so LabVIEW recompiles from the diagram on its next load.

WHY THIS EXISTS. `ConvertAIXMLToVI` emits compiled code - a generated VI is
`SourceOnly="0"` and carries `VICD`, and pylabview copies those blocks through
UNPARSED, which is exactly the property that makes its round trip lossless. So
after any heap edit they go on describing the diagram as it was BEFORE the edit,
and LabVIEW then serves that. Measured 2026-09-10 by comparing bundles:

    NI's instantiated .vit    11 files   SourceOnly="1"   no compiled blocks
    ConvertAIXMLToVI output   18 files   SourceOnly="0"   VICD0/1 BNID CNST
                                                          GCDI NUID SUID LPIN

The symptom without this step is `Error 47, Unknown heap` from
`ConvertVIToAIXML` plus a VI LabVIEW loads and refuses to compile - and
re-generating does NOT help, because the generator writes them itself, before
anything has loaded the file. This is CLAUDE.md's "close the project, not the VI"
rule arriving from a second direction.

WHAT IS DELIBERATELY NOT DROPPED: `TM80` and `DFDS`. Those are the DATA SPACE,
not compiled code, and a VI without them does not load. The list here is the
measured set and not "everything that looks generated".

usage:
  pylv-strip-compiled.py <bundle> <base>
"""
import io, re, sys

# Measured against NI's own source-only file: present in a generated VI, absent
# from an instantiated .vit, and none of them needed to load a VI.
COMPILED = ['VICD', 'GCDI', 'NUID', 'SUID', 'BNID', 'CNST', 'LPIN']


def main(bundle, base):
    p = "%s/%s.xml" % (bundle, base)
    s = io.open(p, encoding='utf-8').read()

    s, n = re.subn(r'SourceOnly="0"', 'SourceOnly="1"', s)
    if n == 0:
        # already source-only, or the attribute moved - say which rather than
        # reporting a success that did nothing.
        already = 'SourceOnly="1"' in s
        print("SourceOnly: %s" % ("already 1" if already
                                  else "ATTRIBUTE NOT FOUND - the bundle may not be a VI"))
    else:
        print("SourceOnly: 0 -> 1")

    dropped, absent = [], []
    for block in COMPILED:
        # the dialect opens a block at two spaces and closes it at four
        m = re.search(r'\n  <%s>.*?\n    </%s>' % (block, block), s, re.S)
        if m is None:
            m = re.search(r'\n  <%s\s*/>' % block, s)
        if m is None:
            absent.append(block)
            continue
        s = s[:m.start()] + s[m.end():]
        dropped.append(block)

    io.open(p, 'w', encoding='utf-8', newline='').write(s)
    print("dropped:  %s" % (", ".join(dropped) if dropped else "(none)"))
    if absent:
        print("absent:   %s" % ", ".join(absent))
    print("LabVIEW will recompile from the diagram on its next load.")


if __name__ == '__main__':
    main(*sys.argv[1:])
