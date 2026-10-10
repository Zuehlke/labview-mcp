# -*- coding: utf-8 -*-
"""Split coverage.py's refusals into what NI's own converter cannot do either and what is a gap
of THIS composer - the honest measure of how far the gRPC-free route is from the gRPC one.

    coverage.py --tsv cov.tsv
    parity.py cov.tsv

The NI side is what this repository has measured or NI documents about ConvertAIXMLToVI:
class-typed data (AIXML has no class identity), Express VI and XNode instances and subVIs it
cannot resolve (`Unsupported SubVI`), Timed Loops (`Unsupported node type`), Call Parent Class
Method (no terminals), and exports NI wrote incomplete (a wire with no source, a Stacked
Sequence with no frames).
"""
import collections
import re
import sys

NI_SHARED = [
    ("class data (AIXML has no class identity)", r"UDClassInst|\.lvclass\b.*not composable|class '\{?UDClass"),
    ("Express VI / XNode instance", r"Ex_Inst_|\.xnode\b|Express VI"),
    ("subVI that cannot be resolved", r"neither near the caller nor in the installation|cannot be resolved|not found in"),
    ("Timed Loop", r"Timed Loop"),
    ("Call Parent Class Method", r"Call Parent Class Method"),
    ("export incomplete (net with no source)", r"has no source"),
    ("Stacked Sequence (NI exports no frames)", r"Stacked Sequence"),
]


def main(argv):
    rows = [l.rstrip("\n").split("\t", 2) for l in open(argv[0], encoding="utf-8") if l.strip()]
    ok = sum(1 for r in rows if r[1] == "OK")
    shared, ours = collections.Counter(), collections.Counter()
    for _, st, msg in (r + [""] * (3 - len(r)) for r in rows):
        if st == "OK":
            continue
        cat = next((name for name, pat in NI_SHARED if re.search(pat, msg)), None)
        if cat:
            shared[cat] += 1
        else:
            ours[re.sub(r"\d+", "N", msg)[:90]] += 1
    n = len(rows)
    print("%d exports: %d compose, %d refused for reasons NI's converter shares, %d refused by "
          "gaps of this composer" % (n, ok, sum(shared.values()), sum(ours.values())))
    print("of the exports NI's converter could at best rebuild (%d), this composer rebuilds %d (%.1f %%)"
          % (n - sum(shared.values()), ok, 100.0 * ok / max(1, n - sum(shared.values()))))
    print("\nshared with NI's converter:")
    for k, v in shared.most_common():
        print("  %4d  %s" % (v, k))
    print("\nthis composer's own gaps (first blocker per VI):")
    for k, v in ours.most_common(40):
        print("  %4d  %s" % (v, k))


if __name__ == "__main__":
    main(sys.argv[1:])
