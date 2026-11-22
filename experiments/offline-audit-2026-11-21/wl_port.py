"""Faithful Python port of wirelint v0.2.0's fact readers (src/formats/rsrc.rs, heap.rs,
connector.rs, xml.rs subset). Independent of pylabview. Prints one JSON line per file."""
import json, os, struct, sys, zlib
import xml.etree.ElementTree as ET

class Bad(Exception):
    pass

def sl(b, off, n):
    if off < 0 or n < 0 or off + n > len(b):
        raise Bad(f"out of bounds {off}+{n}>{len(b)}")
    return b[off:off + n]

def num(b, off):
    return struct.unpack(">I", sl(b, off, 4))[0]

def section_payload(table, table_size, data, cnt_m1, sec_off, need_single=True):
    if need_single and cnt_m1 != 0:
        raise Bad("unsupported section count")
    if sec_off < table_size:
        raise Bad("section overlaps block table")
    sec = sl(table, sec_off, 20)
    if num(sec, 0) != 0:
        raise Bad("unsupported section index")
    off = num(sec, 12)
    rec = sl(data, off, len(data) - off)
    return sl(rec, 4, num(rec, 0))

# ---------------- heap (BDHb) ----------------
ARRAY_ELEMENT, CLASS, DIAGRAM, LABEL, TEXT_HAIR = -6, -2, 27, 10, 1
NODE_LIST, Z_PLANE_LIST, TEXT_RECORD, TEXT, FP_TERM, OBJ_FLAGS = 164, 303, 270, 3, 22, 172
ICON_STYLE = 0x04000000

def _signed(b, c, n):
    v = sl(b, c[0], n); c[0] += n
    return int.from_bytes(v, "big", signed=True)

def _uvar(b, c):
    f = sl(b, c[0], 1)[0]; c[0] += 1
    if f == 255:
        v = sl(b, c[0], 2); c[0] += 2; return int.from_bytes(v, "big")
    if f == 254:
        v = sl(b, c[0], 4); c[0] += 4; return int.from_bytes(v, "big")
    return f

def _svar(b, c):
    v = _signed(b, c, 1)
    if v == -128: return _signed(b, c, 2)
    if v == -127: return _signed(b, c, 4)
    return v

def _sshort(b, c):
    v = _signed(b, c, 2)
    return _signed(b, c, 4) if v == -32768 else v

def read_bdhb(table, ts, data, cnt_m1, sec_off):
    p = section_payload(table, ts, data, cnt_m1, sec_off)
    expected = num(p, 0)
    if expected > 64 * 1024 * 1024:
        raise Bad("BDHb too large")
    d = zlib.decompressobj()
    dec = d.decompress(p[4:])
    if len(dec) != expected or d.unused_data:
        raise Bad("inconsistent compressed BDHb length")
    if num(dec, 0) != len(dec) - 4:
        raise Bad("inconsistent BDHb heap length")
    b = dec[4:]
    c = [0]; stack = []; bookmarks = []; icons = []
    while c[0] < len(b):
        h = sl(b, c[0], 2); c[0] += 2
        size = h[0] >> 5; attrs = h[0] & 0x10; scope = (h[0] >> 2) & 3
        raw = ((h[0] & 3) << 8) | h[1]
        tag = _signed(b, c, 4) if raw == 1023 else raw - 31
        cls = None
        if attrs:
            for _ in range(_uvar(b, c)):
                i = _svar(b, c); v = _sshort(b, c)
                if i == CLASS:
                    if cls is not None: raise Bad("duplicate class attr")
                    cls = v
        if size in (0, 7): ln = 0
        elif 1 <= size <= 4: ln = size
        elif size == 6: ln = _uvar(b, c)
        else: raise Bad("unsupported heap value size")
        content = sl(b, c[0], ln); c[0] += ln
        if scope in (0, 1):
            if tag == TEXT and len(stack) >= 4:
                pa = stack[-4:]
                if (pa[0][1] == DIAGRAM and pa[1][0] in (NODE_LIST, Z_PLANE_LIST)
                        and pa[2] == (ARRAY_ELEMENT, LABEL) and pa[3] == (TEXT_RECORD, TEXT_HAIR)
                        and content.startswith(b"#")):
                    bookmarks.append(content.decode("latin-1"))
            if tag == OBJ_FLAGS and stack and stack[-1] == (ARRAY_ELEMENT, FP_TERM):
                if len(content) > 4: raise Bad("oversized objFlags")
                icons.append(int.from_bytes(content, "big") & ICON_STYLE != 0)
            if scope == 0:
                stack.append((tag, cls))
        elif scope == 2:
            if not stack: raise Bad("unmatched closing tag")
            if stack.pop()[0] != tag: raise Bad("mismatched closing tag")
        else:
            raise Bad("unsupported heap scope")
    if stack: raise Bad("unclosed heap scope")
    return bookmarks, icons

# ---------------- connector pane ----------------
def _var(b, c):
    v = int.from_bytes(sl(b, c[0], 2), "big"); c[0] += 2
    if v & 0x8000:
        lo = int.from_bytes(sl(b, c[0], 2), "big"); c[0] += 2
        v = ((v & 0x7fff) << 16) | lo
    return v

def read_pattern(table, ts, data, conp, vctp):
    cp = section_payload(table, ts, data, *conp)
    if len(cp) != 2: raise Bad("unsupported CONP payload size")
    conp_id = int.from_bytes(cp, "big")
    vp = section_payload(table, ts, data, *vctp)
    exp = num(vp, 0)
    d = zlib.decompressobj(); dec = d.decompress(vp[4:])
    if len(dec) != exp or d.unused_data: raise Bad("inconsistent VCTP length")
    c = [4]; count = num(dec, 0); flat = []
    for _ in range(count):
        start = c[0]; flat.append(start)
        ln = _var(dec, c); sl(dec, c[0], 2); c[0] += 2
        if ln < 4: raise Bad("TD too small")
        c[0] = start + ln
        if c[0] > len(dec): raise Bad("TD exceeds section")
    top = [_var(dec, c) for _ in range(_var(dec, c))]
    if conp_id < 1 or conp_id - 1 >= len(top): raise Bad("CONP index out of range")
    fid = top[conp_id - 1]
    if fid >= len(flat): raise Bad("FlatTypeID out of range")
    c = [flat[fid]]; _var(dec, c); hdr = sl(dec, c[0], 2); c[0] += 2
    if hdr[1] != 0xF0: raise Bad("CONP TD not Function")
    for _ in range(_var(dec, c)): _var(dec, c)
    rest = sl(dec, c[0], 4)
    raw = int.from_bytes(rest[2:4], "big")
    if raw >> 3 >= 36: raise Bad("unknown pattern code")
    return raw >> 3, raw

EMPTY_PW = bytes([0xd4,0x1d,0x8c,0xd9,0x8f,0x00,0xb2,0x04,0xe9,0x80,0x09,0x98,0xec,0xf8,0x42,0x7e])

def read_vi(path):
    b = open(path, "rb").read()
    first = sl(b, 0, 32); ho = 0
    while True:
        h = sl(b, ho, 32)
        if h[:6] != b"RSRC\r\n" or h[6:8] != b"\0\x03" or h[12:16] != b"LBVW" or h[8:12] not in (b"LVIN", b"LVCC", b"sVIN", b"sVCC"):
            raise Bad("unsupported RSRC signature/type")
        if h[:16] != first[:16] or h[20:] != first[20:]:
            raise Bad("inconsistent RSRC headers")
        nxt = num(h, 16)
        if nxt == ho: break
        if nxt < ho + 32: raise Bad("bad header chain")
        ho = nxt
    info = sl(b, ho, num(h, 20)); do = num(h, 24)
    data = sl(b, do, num(h, 28))
    lh = sl(info, 32, 20)
    if num(lh, 8) != 32: raise Bad("bad block-info header size")
    to = num(lh, 12); table = sl(info, to, len(info) - to)
    count = num(table, 0) + 1; ts = count * 12 + 4; sl(table, 0, ts)
    is_vi = h[8:12] in (b"LVIN", b"sVIN")
    f = {"type": h[8:12].decode()}
    conp = vctp = strg = None
    for i in range(count):
        blk = table[4 + 12 * i: 16 + 12 * i]; name = blk[:4]
        if name == b"LVSR":
            p = section_payload(table, ts, data, num(blk, 4), num(blk, 8))
            if len(p) < 68: raise Bad("truncated LVSR")
            fl2 = num(p, 8); f["version"] = "%x" % num(p, 0)
            f["sourceOnly"] = bool(fl2 & (1 << 10))
            if is_vi:
                ins = num(p, 24)
                f["debug"] = bool(ins & (1 << 9)); f["breakpoints"] = bool(ins & 0x20000000)
                f["autoErr"] = bool(fl2 & 0x20000000)
        elif is_vi and name == b"BDHb":
            f["bookmarks"], icons = read_bdhb(table, ts, data, num(blk, 4), num(blk, 8))
            f["termIcon"] = sum(icons); f["termData"] = len(icons) - sum(icons)
        elif is_vi and name in (b"BDHP", b"BDHc"):
            raise Bad("unsupported heap format " + name.decode())
        elif is_vi and name == b"BDPW":
            p = section_payload(table, ts, data, num(blk, 4), num(blk, 8))
            if len(p) < 16 or len(p) % 16: raise Bad("bad BDPW")
            f["password"] = p[:16] != EMPTY_PW
        elif name == b"STRG":
            strg = (num(blk, 4), num(blk, 8))
        elif is_vi and name == b"CONP":
            conp = (num(blk, 4), num(blk, 8))
        elif is_vi and name == b"VCTP":
            vctp = (num(blk, 4), num(blk, 8))
    if "sourceOnly" not in f: raise Bad("missing LVSR")
    # description (version >= 4 layout)
    if strg is None:
        f["desc"] = False
    else:
        cnt_m1, so = strg
        secs = sl(table, so, (cnt_m1 + 1) * 20)
        s0 = [secs[k:k+20] for k in range(0, len(secs), 20) if num(secs[k:k+20], 0) == 0]
        if len(s0) != 1: raise Bad("STRG section 0 missing/duplicate")
        off = num(s0[0], 12); rec = sl(data, off, len(data) - off); p = sl(rec, 4, num(rec, 0))
        d = sl(p, 4, num(p, 0))
        f["desc"] = any((not chr(x).isspace() or x > 127) and x != 0xCA for x in d) if d else False
    if is_vi and conp and vctp:
        f["pattern"], f["patternRaw"] = read_pattern(table, ts, data, conp, vctp)
    return f

def read_xml(path):
    root = ET.parse(path).getroot()
    f = {"type": root.tag}
    def prop(name):
        hits = [p for p in root.findall("Property") if p.get("Name") == name]
        if len(hits) > 1: raise Bad("duplicate owner property " + name)
        return hits[0] if hits else None
    so = prop("NI.LV.All.SourceOnly")
    f["sourceOnly"] = so is not None and (so.text or "").strip() == "true"
    dn = "NI.Project.Description" if root.tag == "Project" else "NI.Lib.Description"
    d = prop(dn)
    lines = [l.strip() for l in ((d.text if d is not None else "") or "").splitlines()]
    f["desc"] = any(l and not l.startswith("#wirelint ") for l in lines)
    g = prop("NI.LVClass.Geneology")
    f["geneology"] = g is not None and (bool((g.text or "").strip()) or len(g) > 0)
    f["version"] = root.get("LVVersion")
    if root.tag == "Library":
        f["members"] = [i.get("URL") for i in root.iter("Item") if i.get("Type") == "VI"]
    return f

if __name__ == "__main__":
    for line in sys.stdin:
        p = line.strip()
        if not p: continue
        ext = os.path.splitext(p)[1].lower()
        try:
            r = read_xml(p) if ext in (".lvproj", ".lvlib", ".lvclass") else read_vi(p)
            r["ok"] = True
        except Exception as e:
            r = {"ok": False, "error": f"{type(e).__name__}: {e}"}
        r["path"] = p
        print(json.dumps(r), flush=True)
