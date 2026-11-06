# -*- coding: utf-8 -*-
"""Print the decoded VCTP bytes of one type descriptor of a VI (original vs round-tripped).

    heap_tdbytes.py <vi> <td index> [<other vi>]
"""
import io
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import heap_rawdiff as rd  # noqa: E402
import heap_rawsweep as rs  # noqa: E402


def td_bytes(path, idx):
    err = sys.stderr
    sys.stderr = io.StringIO()
    try:
        vi, fh = rd.load(path)
        secs = rd.sections(vi)
    finally:
        sys.stderr = err
    fh.close()
    data = next(v[1] for k, v in secs.items() if k[0] == b"VCTP")
    off, size, flags, typ = rs.td_list(data)[idx]
    return data[off:off + size]


def main(argv):
    idx = int(argv[1])
    for p in [argv[0]] + argv[2:]:
        b = td_bytes(p, idx)
        print("%s\n  %d bytes: %s\n  ascii: %s" % (os.path.basename(p), len(b), b.hex(" "),
              "".join(chr(c) if 32 <= c < 127 else "." for c in b)))


if __name__ == "__main__":
    main(sys.argv[1:])
