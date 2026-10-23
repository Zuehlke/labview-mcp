"""Probe: dress a base boolean in a target's images WITHOUT adding or removing a part.

usage: swapparts.py <baseBundleDir> <baseName> <targetBundleDir> <targetName> <outDir> [--ddo]

For every part of the base's stdBool whose partID the target also has, the part's CONTENT
(bounds, colours, image or image table) is taken from the target; the base keeps its own
element, class and uid. Positive ImageResIDs are the target's MNGI images and are copied
over. Parts only one side has are reported, never added or removed. --ddo also takes the
ddo's objFlags (mechanical action among them) and bounds from the target.
"""
import os, re, shutil, sys

base_dir, name, tgt_dir, tname, out_dir = sys.argv[1:6]
take_ddo = "--ddo" in sys.argv[6:]
os.makedirs(out_dir, exist_ok=True)
for f in os.listdir(base_dir):
    shutil.copy(os.path.join(base_dir, f), os.path.join(out_dir, f))


def parts(text):
    """partID -> (start, end, class, element text) of each stdBool part."""
    ddo = re.search(r'<ddo class="stdBool" uid="\d+">.*?<partsList elements="\d+">(.*?)\n *</partsList>', text, re.S)
    body_start = ddo.start(1)
    out = {}
    for m in re.finditer(r'\n( *)<SL__arrayElement class="([^"]+)" uid="\d+">.*?\n\1  </SL__arrayElement>', ddo.group(1), re.S):
        pid = re.search(r'<partID>(\d+)</partID>', m.group(0))
        if pid:
            out[int(pid.group(1))] = (body_start + m.start(), body_start + m.end(), m.group(2), m.group(0))
    return out


fp = os.path.join(out_dir, name + "_FPHb.xml")
t = open(fp, encoding="utf-8").read()
tt = open(os.path.join(tgt_dir, tname + "_FPHb.xml"), encoding="utf-8").read()
bp, tp = parts(t), parts(tt)
report = {"swapped": [], "onlyInTarget": sorted(set(tp) - set(bp)), "onlyInBase": sorted(set(bp) - set(tp)),
          "classMismatch": []}

used = set()
for pid in sorted(set(bp) & set(tp), key=lambda p: -bp[p][0]):  # back to front keeps offsets valid
    s, e, cls, el = bp[pid]
    ts, te, tcls, tel = tp[pid]
    if cls != tcls:
        report["classMismatch"].append((pid, cls, tcls))
        continue
    head = re.match(r'\n *<SL__arrayElement class="[^"]+" uid="\d+">', el).group(0)
    new = head + tel[re.match(r'\n *<SL__arrayElement class="[^"]+" uid="\d+">', tel).end():]
    # keep the base's uid inside nested objects too (label textRec etc. carry none)
    t = t[:s] + new + t[e:]
    # a positive id is an MNGI image; 0 is "no image", negative ids are LabVIEW's own artwork
    used.update(int(i) for i in re.findall(r'<ImageResID>(\d+)</ImageResID>', tel) if int(i) > 0)
    report["swapped"].append(pid)

if "--order" in sys.argv[6:]:
    # z-order: the parts list is drawn in list order, so take the target's order for the
    # parts both have; a part only the base has keeps its slot
    ddo = re.search(r'(<ddo class="stdBool" uid="\d+">.*?<partsList elements="\d+">)(.*?)(\n *</partsList>)', t, re.S)
    now = parts(t)
    els = [now[p][3] for p in sorted(now, key=lambda p: now[p][0])]
    pids = sorted(now, key=lambda p: now[p][0])
    common = [p for p in sorted(tp, key=lambda p: tp[p][0]) if p in now]
    it = iter(common)
    ordered = [now[next(it)][3] if p in tp else now[p][3] for p in pids]
    body = ddo.group(2)
    rest = body
    for e in els:
        rest = rest.replace(e, "", 1)
    assert rest.strip() == "", "parts list holds more than its parts"
    t = t[:ddo.start(2)] + "".join(ordered) + t[ddo.end(2):]
    report["order"] = common

if take_ddo:
    for field in ("objFlags", "bounds"):
        v = re.search(r'<ddo class="stdBool" uid="\d+">\s*<objFlags>(\d+)</objFlags>\s*<bounds>(\([^)]*\))', tt)
        val = v.group(1) if field == "objFlags" else v.group(2)
        t = re.sub(r'(<ddo class="stdBool" uid="\d+">\s*<objFlags>)\d+(</objFlags>\s*<bounds>)\([^)]*\)',
                   lambda m: m.group(1) + (val if field == "objFlags" else re.search(r'<objFlags>(\d+)', m.group(0)).group(1)) +
                   m.group(2) + (val if field == "bounds" else re.search(r'<bounds>(\([^)]*\))', m.group(0)).group(1)), t, count=1)
open(fp, "w", encoding="utf-8", newline="\n").write(t)

# the images the swapped parts refer to
main = os.path.join(out_dir, name + ".xml")
m = open(main, encoding="utf-8").read()
assert "<MNGI>" not in m
sections = []
for i in sorted(used):
    src = os.path.join(tgt_dir, "%s_MNGI%d.png" % (tname, i))
    fn = "%s_MNGI%d.png" % (name, i)
    shutil.copy(src, os.path.join(out_dir, fn))
    sections.append('    <Section Index="%d" PaddingLength="0" Format="png" File="%s" />' % (i, fn))
if sections:
    m = m.replace("  <FPHb>", "  <MNGI>\n    <!-- MNG Image -->\n" + "\n".join(sections) + "\n    </MNGI>\n  <FPHb>", 1)
    open(main, "w", encoding="utf-8", newline="\n").write(m)
report["images"] = sorted(used)
print(report)
