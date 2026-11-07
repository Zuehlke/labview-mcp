# -*- coding: utf-8 -*-
"""Which front-panel object makes a composed VI's panel unloadable (`Cannot load front panel`,
1013)? One probe VI per Control/Indicator of each export - the element alone, unwired, at the top
level, with the reader's trailer lines for its uid - composed and listed for cov_load.py. A probe
that does not load executable names the object.

    panel_probes.py <out dir> <export.xml> [...]
"""
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
COMPOSER = os.path.normpath(os.path.join(HERE, "..", "..", "scripts", "aixml-pylabview"))
PY = os.path.normpath(os.path.join(HERE, "..", "..", "tools", "pylabview", "runtime", "python.exe"))


def main(argv):
    out = argv[0]
    os.makedirs(os.path.join(out, "vis"), exist_ok=True)
    rows = []
    for src in argv[1:]:
        text = open(src, encoding="utf-8").read()
        body, tail = text.rsplit("</VI>", 1)
        tag = os.path.splitext(os.path.basename(src))[0]
        for m in re.finditer(r"<(Control|Indicator) [^\n]*?/>", body):
            el = m.group(0)
            uid = re.search(r'uid="(\d+)"', el).group(1)
            el = re.sub(r'uid_parent="[^"]*"', 'uid_parent="root"', el)
            el = re.sub(r'(inputs|outputs)="[^"]*"', lambda x: '%s="value:"' % x.group(1), el)
            el = re.sub(r'\s(conIdx|connection)="[^"]*"', "", el)
            pis = [p for p in re.findall(r"<\?lvmcp[^\n]*\?>", tail) if 'uid="%s"' % uid in p]
            name = "%s_%s" % (tag, uid)
            x = os.path.join(out, name + ".xml")
            open(x, "w", encoding="utf-8").write(
                '<VI _name="%s.vi" description="x">\n  %s\n</VI>\n%s\n' % (name, el, "\n".join(pis)))
            vi = os.path.join(out, "vis", name + ".vi")
            subprocess.run([PY, "-W", "ignore::SyntaxWarning", os.path.join(COMPOSER, "aixml2vi.py"), x, vi],
                           capture_output=True, text=True, stdin=subprocess.DEVNULL)
            if os.path.exists(vi):
                label = re.search(r'_name="([^"]*)"', el).group(1)
                t = re.search(r'type="([^"]*)"', el).group(1)
                st = re.search(r'style="([^"]*)"', el)
                rows.append("%s\t%s | %s | %s%s" % (os.path.normpath(vi), tag, label, t,
                                                    " | style " + st.group(1) if st else ""))
    with open(os.path.join(out, "composed.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(rows) + "\n")
    print(len(rows), "probes")


if __name__ == "__main__":
    main(sys.argv[1:])
