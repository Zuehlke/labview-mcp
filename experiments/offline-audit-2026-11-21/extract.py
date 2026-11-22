"""Extract VIs with pylabview's readRSRC into ref/<n>/ (one folder per file). Args: list file, start, count."""
import os, subprocess, sys
S = os.path.dirname(os.path.abspath(__file__))
PY = r"C:\Projects\LabVIEWMCP\tools\pylabview\vendor\pylabview\readRSRC.py"
env = dict(os.environ, PYTHONPATH=r"C:\Projects\LabVIEWMCP\tools\pylabview\vendor")
paths = [l.strip() for l in open(sys.argv[1], encoding="utf-8") if l.strip()]
start, count = int(sys.argv[2]), int(sys.argv[3])
tag = os.path.splitext(os.path.basename(sys.argv[1]))[0]
for i, p in enumerate(paths[start:start + count], start):
    d = os.path.join(S, "ref", f"{tag}_{i:04d}")
    os.makedirs(d, exist_ok=True)
    open(os.path.join(d, "src.txt"), "w", encoding="utf-8").write(p)
    r = subprocess.run([sys.executable, PY, "-x", "-i", p, "-m", os.path.join(d, "vi.xml")],
                       env=env, capture_output=True, text=True, timeout=300)
    if r.returncode:
        open(os.path.join(d, "err.txt"), "w").write(r.stderr[-2000:])
    print(i, r.returncode, flush=True)
