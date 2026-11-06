# -*- coding: utf-8 -*-
"""heap_rawdiff.py over two whole trees: for every file present in both, which block sections
DECODE differently after the round trip (raw differences from recompression are ignored). For VCTP
the differing type descriptor is named (index, full type, label).

    heap_rawsweep.py <original tree> <round-tripped tree> <out.tsv> [k/n]
"""
import io
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import heap_rawdiff as rd  # noqa: E402

EXT = (".vi", ".vim", ".ctl")


def td_list(data):
    """(offset, size, flags, type) of each TD in decoded VCTP data (LV 8+ layout: count, then TDs)."""
    out = []
    try:
        n = int.from_bytes(data[0:4], "big")
        p = 4
        for _ in range(n):
            size = int.from_bytes(data[p:p + 2], "big")
            if size < 4:
                break
            out.append((p, size, data[p + 2], data[p + 3]))
            p += size
    except Exception:   # noqa: BLE001
        pass
    return out


def vctp_culprit(da, db):
    ta, tb = td_list(da), td_list(db)
    for i, (x, y) in enumerate(zip(ta, tb)):
        if da[x[0]:x[0] + x[1]] != db[y[0]:y[0] + y[1]]:
            return "TD %d type 0x%02x flags 0x%02x size %d->%d" % (i, x[3], x[2], x[1], y[1])
    return "TD lists %d vs %d" % (len(ta), len(tb))


def main(argv):
    a, b, out = argv[0], argv[1], argv[2]
    k, n = (int(x) for x in argv[3].split("/")) if len(argv) > 3 else (0, 1)
    files = sorted(os.path.relpath(os.path.join(dp, f), a) for dp, _, fn in os.walk(a)
                   for f in fn if f.lower().endswith(EXT))
    with open(out, "w", encoding="utf-8") as log:
        for i, rel in enumerate(files):
            if i % n != k:
                continue
            pa, pb = os.path.join(a, rel), os.path.join(b, rel)
            if not os.path.exists(pb):
                continue
            try:
                stderr = sys.stderr
                sys.stderr = io.StringIO()
                try:
                    (va, fa), (vb, fb) = rd.load(pa), rd.load(pb)
                    sa, sb = rd.sections(va), rd.sections(vb)
                finally:
                    sys.stderr = stderr
                fa.close()
                fb.close()
            except Exception as ex:   # noqa: BLE001
                log.write("\t".join([rel, "LOADFAIL", str(ex)[:150]]) + "\n")
                continue
            diffs = []
            for key in sorted(set(sa) | set(sb), key=str):
                if key not in sa or key not in sb:
                    diffs.append("%s missing" % (key[0].decode(errors="replace")))
                    continue
                if sa[key][1] != sb[key][1]:
                    name = key[0].decode(errors="replace")
                    diffs.append(name + (": " + vctp_culprit(sa[key][1], sb[key][1]) if name == "VCTP" else ""))
            log.write("\t".join([rel, "SAME" if not diffs else "DIFF", "; ".join(diffs)]) + "\n")
            log.flush()


if __name__ == "__main__":
    main(sys.argv[1:])
