"""Probe: turn an extracted plain boolean .ctl bundle into a picture button.

usage: picbool.py <baseBundleDir> <baseName> <outDir> <w> <h> <png0> [<png1> ...]
png order = multiCosm table order (False, True, True->False, False->True, ...).
"""
import os, re, shutil, sys

base_dir, name, out_dir, w, h = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4]), int(sys.argv[5])
pngs = sys.argv[6:]
os.makedirs(out_dir, exist_ok=True)
for f in os.listdir(base_dir):
    shutil.copy(os.path.join(base_dir, f), os.path.join(out_dir, f))

main = os.path.join(out_dir, name + ".xml")
m = open(main, encoding="utf-8").read()
first_id = 2
sections = []
for i, p in enumerate(pngs):
    fn = "%s_MNGI%d.png" % (name, first_id + i)
    shutil.copy(p, os.path.join(out_dir, fn))
    sections.append('    <Section Index="%d" PaddingLength="0" Format="png" File="%s" />' % (first_id + i, fn))
mngi = "  <MNGI>\n    <!-- MNG Image -->\n" + "\n".join(sections) + "\n    </MNGI>\n"
assert "<MNGI>" not in m
m = m.replace("  <FPHb>", mngi + "  <FPHb>", 1)
open(main, "w", encoding="utf-8", newline="\n").write(m)

fp = os.path.join(out_dir, name + "_FPHb.xml")
t = open(fp, encoding="utf-8").read()
# the stdBool's partsList: keep the label and the annex, drop every cosmetic part
pl = re.search(r'(<ddo class="stdBool" uid="\d+">.*?<partsList elements=")(\d+)(">)(.*?)(\n *</partsList>)', t, re.S)
assert pl, "no stdBool partsList"
body = pl.group(4)
elems = re.findall(r'\n              <SL__arrayElement class="[^"]+" uid="\d+">.*?\n                </SL__arrayElement>|\n              <SL__arrayElement class="annex" uid="\d+">.*?\n                </SL__arrayElement>', body, re.S)
label = [e for e in elems if 'class="label"' in e][0]
annex = re.search(r'\n              <SL__arrayElement class="annex" uid="\d+">.*?</SL__arrayElement>', body, re.S).group(0)
uids = [int(u) for u in re.findall(r'uid="(\d+)"', t)]
nu = max(uids) + 1
table = "".join(
    '\n                  <SL__arrayElement class="Image">\n                    <ImageResID>%d</ImageResID>\n                    </SL__arrayElement>' % (first_id + i)
    for i in range(len(pngs)))
cosm = ('\n              <SL__arrayElement class="multiCosm" uid="%d">'
        '\n                <objFlags>2326</objFlags>'
        '\n                <partID>21</partID>'
        '\n                <howGrow>240</howGrow>'
        '\n                <bounds>(15, 0, %d, %d)</bounds>'
        '\n                <fgColor>00E2E2E2</fgColor>'
        '\n                <bgColor>00E2E2E2</bgColor>'
        '\n                <table elements="%d">%s'
        '\n                  </table>'
        '\n                </SL__arrayElement>') % (nu, 15 + h, w, len(pngs), table)
new_body = label + cosm + annex
t = t[:pl.start(2)] + "3" + pl.group(3) + new_body + t[pl.end(4):]
# ddo bounds: keep top-left, size to label + image
db = re.search(r'(<ddo class="stdBool" uid="\d+">\s*<objFlags>\d+</objFlags>\s*<bounds>)\((-?\d+), (-?\d+), (-?\d+), (-?\d+)\)', t)
top, left = int(db.group(2)), int(db.group(3))
t = t[:db.start(2) - 1] + "(%d, %d, %d, %d)" % (top, left, top + 15 + h, left + max(w, 47)) + t[db.end(5) + 1:]
open(fp, "w", encoding="utf-8", newline="\n").write(t)
print("ok: %d images, new multiCosm uid %d" % (len(pngs), nu))
