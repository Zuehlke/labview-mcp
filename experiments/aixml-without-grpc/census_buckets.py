import collections, re, sys, os

rows = [l.rstrip("\n").split("\t") for l in open(sys.argv[1], encoding="utf-8") if l.count("\t") >= 5]
ROOT = "examples\\"

def area(path):
    p = path.replace("/", "\\")
    i = p.find(ROOT)
    rest = p[i + len(ROOT):] if i >= 0 else p
    return rest.split("\\")[0]

def bucket(msg):
    m = msg
    rules = [
        (r"^Call .*neither near the caller nor in the installation", "Call target not found"),
        (r"^Call .*has no (input|output)", "Call: terminal not on callee pane"),
        (r"^Call .*lists no member", "Call: class lists no member"),
        (r"^Call .*XNode", "Call into XNode"),
        (r"^Call .*dynamic-dispatch", "Call: dynamic dispatch not composable"),
        (r"^Call By Reference", "Call By Reference without pane"),
        (r"^Property Node .*class '([^']*)' is not composable", r"Property Node: class \1"),
        (r"^Property Node .*property .* of (\S+) is not in the harvested table", r"Property Node: property of \1 not harvested"),
        (r"^Property Node .*no property node on (\S+)", r"Property Node: no part for \1"),
        (r"^Invoke Node .*class '([^']*)' is not composable", r"Invoke Node: class \1"),
        (r"^Invoke Node .*no invoke node (\S+)", r"Invoke Node: method not harvested"),
        (r"^Invoke Node .*outputs is not composable", "Invoke Node: method with several outputs"),
        (r"^Event Structure .*event '([^']*)' is not composable", r"Event Structure: event \1"),
        (r"^Event Structure .*the (\w+) event '([^']*)'", r"Event Structure: \1 event \2"),
        (r"^Event Structure", "Event Structure: other"),
        (r"^Event Data Node .*no registration carries", "Event Data Node: user event not registered"),
        (r"^Event Data Node .*CtlRef", "Event Data Node: CtlRef of control type"),
        (r"^Event Data Node .*data of a '?([^' ]*)'? event", r"Event Data Node: data of \1 event"),
        (r"^Event Data Node", "Event Data Node: other"),
        (r"^Event Filter Node", "Event Filter Node"),
        (r"^Case Structure .*string ranges", "Case: string ranges"),
        (r"^Case Structure .*integer case on a (\S+) wire", r"Case: integer case on \1 wire"),
        (r"^Case Structure .*enum selectors on", "Case: enum selector on other wire"),
        (r"^Case Structure .*no case labels", "Case: old file without case labels"),
        (r"^Case Structure .*only a boolean", "Case: only boolean composable"),
        (r"^Case Structure", "Case: other"),
        (r"^Conditional Disable", "Conditional Disable condition"),
        (r"ShiftReg .*initialized", "Shift register not initialized"),
        (r"ends at stop .*For Loop", "For Loop conditional stop"),
        (r"^Tunnel .*cond=", "Conditional tunnel"),
        (r"^Tunnel .*mode=", "Tunnel mode"),
        (r"^Tunnel .*type of .* not known", "Tunnel: type unknown"),
        (r"^case tunnel .*cond", "Case tunnel cond"),
        (r"no typed wire reaches", "Tunnel: no typed wire"),
        (r"^Index Array", "Index Array variant"),
        (r"^Build Array", "Build Array variant"),
        (r"^Array To Cluster", "Array To Cluster"),
        (r"^Compound Arithmetic", "Compound Arithmetic operation"),
        (r"^Expression Node", "Expression Node"),
        (r"NSCR link", "MATLAB/Script node"),
        (r"^Node '([^']*)' is not a primitive", r"Unknown primitive: \1"),
        (r"^Node .*no template", "Node: no terminal template"),
        (r"^Node .*mode .* not composable", "Node mode"),
        (r"^Node .* is not composable yet", "Node not composable"),
        (r"^(Bundle|Unbundle)( By Name)? .*no field", "Bundle/Unbundle By Name: field not found"),
        (r"^(Bundle|Unbundle).*type of the cluster", "Bundle/Unbundle: cluster type unknown"),
        (r"^Local Variable", "Local Variable: control not found"),
        (r"^VI Server Reference .*no control", "Control reference: control not found"),
        (r"^VI Server Reference", "Control reference: other"),
        (r"reference to a .* control is not composable", "Reference to control type"),
        (r"^no part for a (\S+) refnum", r"No part: \1 refnum"),
        (r"^no const part", "No part: refnum constant"),
        (r"^no part harvested for FixedConst", "No part: FixedConst"),
        (r"^no part harvested for '?([^' ]*)", r"No part: \1"),
        (r"FixedConst .* not composable", "FixedConst"),
        (r"net .* carries a (\S+), which no composed wire", r"Wire type never composed: \1"),
        (r"net .* ends at .* which is not composable", "Net ends at non-composable terminal"),
        (r"^type '([^'{]*)", r"Type not composable: \1"),
        (r"type '([^'{]*)[^']*' is not composable", r"Type not composable: \1"),
        (r"is not composable yet", "other: not composable yet"),
        (r"element <(\w+)>", r"Element <\1>"),
        (r"structure '([^']*)'", r"Structure \1"),
        (r"^Constant .*class constant", "Class constant not feeding a Call"),
        (r"ring|enum item with a quote", "Ring/enum item with quote"),
        (r"style ", "Control style"),
        (r"sits in .*not a composable diagram", "Structure inside non-composable diagram"),
    ]
    for pat, rep in rules:
        mm = re.search(pat, m)
        if mm:
            return mm.expand(rep) if "\\" in rep else rep
    return "OTHER: " + re.sub(r"'[^']*'", "X", re.sub(r"\d+", "N", m))[:90]

kinds = collections.Counter()
by_area = collections.defaultdict(collections.Counter)
examples = collections.defaultdict(list)
status_area = collections.defaultdict(collections.Counter)
for idx, st, stage, msg, vi, dt in rows:
    a = area(vi)
    status_area[a][st] += 1
    if st in ("REFUSED", "ERROR", "READFAIL"):
        b = (st + ": " if st != "REFUSED" else "") + (bucket(msg) if st == "REFUSED" else msg[:100])
        kinds[b] += 1
        by_area[b][a] += 1
        if len(examples[b]) < 2:
            examples[b].append(os.path.basename(vi.replace("\\", "/")) + " :: " + msg[:160])

print("== status by example area")
for a, c in sorted(status_area.items(), key=lambda kv: -sum(kv[1].values())):
    tot = sum(c.values())
    print("%-32s %4d  ok %4d (%3.0f%%)  refused %3d  readfail %2d  error %d" % (a[:32], tot, c["OK"], 100.0 * c["OK"] / tot, c["REFUSED"], c["READFAIL"], c["ERROR"]))
print()
print("== first blocker per VI")
for b, n in kinds.most_common():
    top = ", ".join("%s %d" % (k[:18], v) for k, v in by_area[b].most_common(3))
    print("%4d  %-60s [%s]" % (n, b[:60], top))
    for e in examples[b]:
        print("        e.g. " + e)
