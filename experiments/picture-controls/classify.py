import os, re, sys, collections
root = sys.argv[1]
rows = []
for n in sorted(os.listdir(root)):
    d = os.path.join(root, n)
    fp = os.path.join(d, n + "_FPHb.xml")
    if not os.path.isfile(fp):
        continue
    t = open(fp, encoding="utf-8", errors="replace").read()
    classes = re.findall(r'class="([A-Za-z0-9_]+)"', t)
    c = collections.Counter(classes)
    # first control-like class after the panel
    ctl = [x for x in classes if x.startswith("std") or x in ("typeDef", "boolean", "pict")]
    imgs = [f for f in os.listdir(d) if f.startswith(n + "_MNGI") or f.startswith(n + "_PICC")]
    other = [f for f in os.listdir(d) if f.endswith(".png") and "ICON" not in f and "icl" not in f and "MNGI" not in f]
    main = os.path.join(d, n + ".xml")
    m = open(main, encoding="utf-8", errors="replace").read()
    tdv = re.search(r'TypeDefVI="(\d)"|<TypeDefVI>(\d)', m)
    rows.append((n, ctl[:3], len(imgs), len(other), sorted(set(x for x in classes if x.startswith("std")))))
for r in rows:
    print(f"{r[0]:28} imgs={r[2]:2} other={r[3]} std={','.join(r[4])}")
