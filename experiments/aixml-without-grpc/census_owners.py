"""Which broken composed VIs are members of a .lvclass / .lvlib (owned VIs, composed OUT of their owner)?"""
import collections, glob, os, re, sys, html

def owners(root):
    """Map normalised VI path -> owner file for every .lvclass/.lvlib under root."""
    own = {}
    for ext in ("*.lvclass", "*.lvlib"):
        for f in glob.glob(os.path.join(root, "**", ext), recursive=True):
            try:
                text = open(f, encoding="utf-8", errors="ignore").read()
            except OSError:
                continue
            base = f   # a LabVIEW URL is relative to the owner FILE: "../X.vi" is beside it
            for m in re.finditer(r'<Item Name="[^"]*\.vi" Type="VI" URL="([^"]+)"', text):
                p = os.path.normcase(os.path.normpath(os.path.join(base, html.unescape(m.group(1)))))
                own.setdefault(p, f)
    return own

def main():
    root, loadtxt = sys.argv[1], sys.argv[2]
    own = owners(root)
    rows = open(loadtxt, encoding="utf-8").read().splitlines()[1:]
    kind = collections.Counter()
    for l in rows:
        parts = l.split(None, 2)
        if len(parts) < 3 or parts[1] == "CRASH":
            continue
        src = os.path.normcase(os.path.normpath(parts[2]))
        o = own.get(src)
        kind["class member" if o and o.endswith(".lvclass") else "library member" if o else "free VI"] += 1
    print(kind)

    # all composed VIs by ownership, executable or not
    bad = set()
    for l in rows:
        parts = l.split(None, 2)
        if len(parts) == 3:
            bad.add(os.path.normcase(os.path.normpath(parts[2])))
    tot, ok = collections.Counter(), collections.Counter()
    for l in open(os.path.join(os.path.dirname(loadtxt), "composed.txt"), encoding="utf-8"):
        if "\t" not in l:
            continue
        src = os.path.normcase(os.path.normpath(l.rstrip("\n").split("\t")[1]))
        o = own.get(src)
        k = "class member" if o and o.endswith(".lvclass") else "library member" if o else "free VI"
        tot[k] += 1
        if src not in bad:
            ok[k] += 1
    for k in tot:
        print("%-15s composed %4d  executable %4d (%.0f%%)" % (k, tot[k], ok[k], 100.0 * ok[k] / tot[k]))



if __name__ == "__main__":
    main()
