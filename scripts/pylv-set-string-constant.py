# -*- coding: utf-8 -*-
"""Set the value of a STRING constant on a diagram, in a pylabview bundle.

Used to give each event frame its own queue command in the Producer/Consumer
(Events) pattern: the template ships one constant reading "element", and a
cloned frame arrives carrying a copy of it.

MEASURED 2026-09-10. A string constant keeps its value TWICE:

  <dco class="bDConstDCO" uid="260">
    <ddo class="stdString" uid="73">
      ... <text>"element"</text>          <- what is DRAWN on the diagram
    <ConstValue>00000007656C656D656E74</ConstValue>   <- the actual VALUE

`ConstValue` is plain hex: a 4-byte big-endian length followed by the bytes.
`00000007` + `656C656D656E74` is 7 + "element". Editing only the `<text>` leaves
the value untouched - the diagram would read "Start" and enqueue "element".
That is the same two-places trap as renaming a control (see
docs/labview-vit-templates.md section 4), and it is silent in both directions.

Lengths may change; pylabview re-serialises the block.

usage:
  pylv-set-string-constant.py <bundle> <base> <diagUid> <newValue>

The constant is addressed by the diagram that contains it, so a freshly cloned
frame needs no uid bookkeeping from the caller.
"""
import io, re, sys


def main(bundle, base, diag, value):
    p = "%s/%s_BDHb.xml" % (bundle, base)
    s = io.open(p, encoding='utf-8').read()

    start = s.index('<SL__arrayElement class="diag" uid="%s">' % diag)
    # the frame ends where the next sibling diag begins, or at its own close
    nxt = s.find('<SL__arrayElement class="diag" uid="', start + 10)
    end = nxt if nxt != -1 else len(s)

    c = s.find('<dco class="bDConstDCO"', start, end)
    assert c != -1, "no bDConstDCO inside diag %s" % diag

    raw = value.encode('ascii')
    hexval = '%08X' % len(raw) + ''.join('%02X' % b for b in bytearray(raw))

    # The VALUE is mandatory; find it first and use it to bound everything else.
    v = s.find('<ConstValue>', c, end)
    assert v != -1, "no ConstValue for the constant in diag %s" % diag
    v_end = s.index('</ConstValue>', v)
    old = s[v + len('<ConstValue>'):v_end]

    # The DRAWN text is optional - a constant authored from AIXML with no _name
    # carries no label at all. Search only between the dco and its ConstValue,
    # or the scan runs on into an unrelated object and renames that instead.
    t = s.find('<text>"', c, v)
    if t != -1:
        t_end = s.index('</text>', t)
        s = s[:t] + '<text>"%s"' % value + s[t_end:]
        # the edit above moved everything after it
        v = s.index('<ConstValue>', s.index('<dco class="bDConstDCO"', start))
        v_end = s.index('</ConstValue>', v)

    s = s[:v + len('<ConstValue>')] + hexval + s[v_end:]

    io.open(p, 'w', encoding='utf-8', newline='').write(s)
    print("OK diag %s: string constant %r -> %r  (ConstValue %s -> %s)"
          % (diag, old, value, old, hexval))


if __name__ == '__main__':
    main(*sys.argv[1:])
