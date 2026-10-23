"""Probe: turn an extracted text-ring .ctl bundle into a picture ring.

usage: picring.py <baseBundleDir> <baseName> <outDir> <w> <h> <ddoFlags|keep> <png0> [<png1> ...]
one png per ring item, in item order.
"""
import os, re, shutil, sys

base_dir, name, out_dir, w, h, flags = sys.argv[1:7]
w, h = int(w), int(h)
pngs = sys.argv[7:]
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
m = m.replace("  <FPHb>", "  <MNGI>\n    <!-- MNG Image -->\n" + "\n".join(sections) + "\n    </MNGI>\n  <FPHb>", 1)
open(main, "w", encoding="utf-8", newline="\n").write(m)

fp = os.path.join(out_dir, name + "_FPHb.xml")
t = open(fp, encoding="utf-8").read()
ml = re.search(r'\n( *)<SL__arrayElement class="multiLabel" uid="\d+">.*?\n\1  </SL__arrayElement>', t, re.S)
if ml is None:
    ml = re.search(r'\n( *)<SL__arrayElement class="multiLabel" uid="\d+">.*?\n *</SL__arrayElement>', t, re.S)
assert ml, "no multiLabel (text ring items) found"
ind = ml.group(1)
uids = [int(u) for u in re.findall(r'uid="(\d+)"', t)]
nu = max(uids) + 1
subs = "".join(
    ('\n{i}    <SL__arrayElement class="SubCosm">'
     '\n{i}      <Bounds>(0, 0, {h}, {w})</Bounds>'
     '\n{i}      <FGColor>00E2E2E2</FGColor>'
     '\n{i}      <BGColor>00E2E2E2</BGColor>'
     '\n{i}      <Image class="Image">'
     '\n{i}        <ImageResID>{r}</ImageResID>'
     '\n{i}        </Image>'
     '\n{i}      </SL__arrayElement>').format(i=ind, h=h, w=w, r=first_id + k)
    for k in range(len(pngs)))
pic = ('\n{i}<SL__arrayElement class="bigMultiCosm" uid="{u}">'
       '\n{i}  <objFlags>2354</objFlags>'
       '\n{i}  <partID>14</partID>'
       '\n{i}  <masterPart>9</masterPart>'
       '\n{i}  <howGrow>15</howGrow>'
       '\n{i}  <bounds>(15, 0, {b}, {r})</bounds>'
       '\n{i}  <fgColor>00E2E2E2</fgColor>'
       '\n{i}  <bgColor>00E2E2E2</bgColor>'
       '\n{i}  <table elements="{n}">{subs}'
       '\n{i}    </table>'
       '\n{i}  </SL__arrayElement>').format(i=ind, u=nu, b=15 + h, r=w, n=len(pngs), subs=subs)
frame = ('\n{i}<SL__arrayElement class="cosm" uid="{u}">'
         '\n{i}  <objFlags>2358</objFlags>'
         '\n{i}  <partID>28</partID>'
         '\n{i}  <masterPart>9</masterPart>'
         '\n{i}  <howGrow>240</howGrow>'
         '\n{i}  <bounds>(20, 2, 46, 68)</bounds>'
         '\n{i}  <image class="Image">'
         '\n{i}    <ImageResID>-10</ImageResID>'
         '\n{i}    </image>'
         '\n{i}  <fgColor>01000000</fgColor>'
         '\n{i}  <bgColor>01000000</bgColor>'
         '\n{i}  </SL__arrayElement>').format(i=ind, u=nu + 1)
if os.environ.get("RING_FRAME") == "1":
    pic += frame
    t = re.sub(r'(<partsList elements=")7(">)', r'\g<1>8\g<2>', t, count=1)
    # geometry of the original picture ring: ddo 48 x 70, body part 9 at (18,0,48,70)
    t = re.sub(r'(<ddo class="stdRing" uid="\d+">\s*<objFlags>\d+</objFlags>\s*<bounds>)\([^)]*\)',
               r'\g<1>(32, 50, 80, 120)', t, count=1)
    t = re.sub(r'(<partID>9</partID>\s*<howGrow>\d+</howGrow>\s*<bounds>)\([^)]*\)', r'\g<1>(18, 0, 48, 70)', t, count=1)
    pic = pic.replace("<bounds>(15, 0, %d, %d)</bounds>" % (15 + h, w), "<bounds>(13, -34, 53, 105)</bounds>")
t = t[:ml.start()] + pic + t[ml.end():]
if flags != "keep":
    t = re.sub(r'(<ddo class="stdRing" uid="\d+">\s*<objFlags>)\d+', r'\g<1>' + flags, t, count=1)
open(fp, "w", encoding="utf-8", newline="\n").write(t)
print("ok: %d items, picture part uid %d" % (len(pngs), nu))
