"""Writes scripts/lvbd_layout_apply.xml - edit this, not the XML.

Six passes (moves, structure sizes, tunnels, shift registers, case selectors, wire clean-up) share one diagram
walk, so the helper is generated:

    python scripts/lvbd_layout_apply_gen.py scripts/lvbd_layout_apply.xml

docs/diagram-layout.md has what each pass does and why.
"""
import sys

out = []
uid = [4200]


def nu():
    uid[0] += 1
    return uid[0]


def e(s):
    out.append(s)


def const(parent, name, typ, value):
    u = nu()
    e(f'  <Constant _name="{name}" outputs="value:{u}.value" type="{typ}" uid="{u}" uid_parent="{parent}" value="{value}"/>')
    return f"{u}.value"


def walk(parent, path_net, top_net):
    """Diagram reached by the path 'o f o f ...' from the top-level diagram. Returns (diagram net, error net)."""
    fmt = const(parent, "int format", "string", "%d")
    ty = const(parent, "int array", "array{int32}", "[]")
    sp = const(parent, "space", "string", " ")
    two = const(parent, "two", "int32", "2")
    u = nu()
    e(f'  <Node _name="Spreadsheet String To Array" inputs="format string:{fmt},spreadsheet string:{path_net},array type (2D Dbl):{ty},delimiter (Tab):{sp}" outputs="array:{u}.ints" uid="{u}" uid_parent="{parent}"/>')
    ints = f"{u}.ints"
    u = nu()
    e(f'  <Node _name="Array Size" inputs="array:{ints}" outputs="size(s):{u}.n" uid="{u}" uid_parent="{parent}"/>')
    n = f"{u}.n"
    u = nu()
    e(f'  <Node _name="Quotient &amp; Remainder" inputs="x:{n},y:{two}" outputs="x-y*floor(x/y):,floor(x/y):{u}.pairs" uid="{u}" uid_parent="{parent}"/>')
    pairs = f"{u}.pairs"
    noerr = const(parent, "no error", "cluster{bool.status,int32.code,string.source}", "[false,0,]")
    lp = nu()
    e(f'  <Structure _name="For Loop" count="{lp}.value" maxin="{pairs}" maxout="" uid="{lp}" uid_parent="{parent}">')
    ti = nu()
    e(f'    <Tunnel _id="In1" inputs="value:{ints}" outputs="value:{ti}.ints" uid="{ti}" uid_parent="{lp}"/>')
    sr = nu()
    l_, r_ = nu(), nu()
    # filled after the body
    body_start = len(out)
    st_cls = const(lp, "structure class", "ref{LV.Structure}", "")
    two2 = const(lp, "pair", "int32", "2")
    u = nu()
    e(f'    <Node _name="Multiply" inputs="x:{lp}.value,y:{two2}" outputs="x*y:{u}.a" uid="{u}" uid_parent="{lp}"/>')
    a = f"{u}.a"
    u = nu()
    e(f'    <Node _name="Increment" inputs="x:{a}" outputs="x+1:{u}.b" uid="{u}" uid_parent="{lp}"/>')
    b = f"{u}.b"
    u = nu()
    e(f'    <Node _name="Index Array" inputs="array:{ti}.ints,index:{a},index:{b}" outputs="element:{u}.oi,element:{u}.fi" uid="{u}" uid_parent="{lp}"/>')
    oi, fi = f"{u}.oi", f"{u}.fi"
    srd = nu()
    ld, rd = nu(), nu()
    u = nu()
    e(f'    <Node _name="Property Node" fields="read+All Objects[]" type="{{LV.Diagram}}" inputs="reference:{ld}.d,error in (no error):{l_}.err" outputs="All Objects[]:{u}.objs,reference out:,error out:{u}.err" uid="{u}" uid_parent="{lp}"/>')
    objs, err = f"{u}.objs", f"{u}.err"
    u = nu()
    e(f'    <Node _name="Index Array" inputs="array:{objs},index:{oi}" outputs="element:{u}.obj" uid="{u}" uid_parent="{lp}"/>')
    obj = f"{u}.obj"
    u = nu()
    e(f'    <Node _name="To More Specific Class" inputs="reference:{obj},error in:{err},target class:{st_cls}" outputs="specific class reference:{u}.st,error out:{u}.err" uid="{u}" uid_parent="{lp}"/>')
    stn, err = f"{u}.st", f"{u}.err"
    u = nu()
    e(f'    <Node _name="Property Node" fields="read+Diagrams[]" type="{{LV.Structure}}" inputs="reference:{stn},error in (no error):{err}" outputs="Diagrams[]:{u}.diags,reference out:,error out:{u}.err" uid="{u}" uid_parent="{lp}"/>')
    diags, err = f"{u}.diags", f"{u}.err"
    u = nu()
    e(f'    <Node _name="Index Array" inputs="array:{diags},index:{fi}" outputs="element:{u}.nd" uid="{u}" uid_parent="{lp}"/>')
    nd = f"{u}.nd"
    e(f'    <ShiftReg uid="{srd}" uid_parent="{lp}">')
    e(f'      <Left inputs="value:{top_net}" outputs="value:{ld}.d" uid="{ld}" uid_parent="{srd}"/>')
    e(f'      <Right inputs="value:{nd}" outputs="value:{rd}.d" uid="{rd}" uid_parent="{srd}"/>')
    e('    </ShiftReg>')
    e(f'    <ShiftReg uid="{sr}" uid_parent="{lp}">')
    e(f'      <Left inputs="value:{noerr}" outputs="value:{l_}.err" uid="{l_}" uid_parent="{sr}"/>')
    e(f'      <Right inputs="value:{err}" outputs="value:{r_}.err" uid="{r_}" uid_parent="{sr}"/>')
    e('    </ShiftReg>')
    e('  </Structure>')
    return f"{rd}.d", f"{r_}.err"


def split(parent, op_net, count):
    fmt = const(parent, "text format", "string", "%s")
    ty = const(parent, "text array", "array{string.String}", "[]")
    sc = const(parent, "semicolon", "string", ";")
    u = nu()
    e(f'  <Node _name="Spreadsheet String To Array" inputs="format string:{fmt},spreadsheet string:{op_net},array type (2D Dbl):{ty},delimiter (Tab):{sc}" outputs="array:{u}.parts" uid="{u}" uid_parent="{parent}"/>')
    parts = f"{u}.parts"
    idx = [const(parent, f"field {i}", "int32", str(i)) for i in range(count)]
    u = nu()
    ins = ",".join(f"index:{i}" for i in idx)
    outs = ",".join(f"element:{u}.f{i}" for i in range(count))
    e(f'  <Node _name="Index Array" inputs="array:{parts},{ins}" outputs="{outs}" uid="{u}" uid_parent="{parent}"/>')
    return [f"{u}.f{i}" for i in range(count)]


def number(parent, s_net):
    u = nu()
    e(f'  <Node _name="Decimal String To Number" inputs="string:{s_net},offset:,default (0L):" outputs="offset past number:,number:{u}.n" uid="{u}" uid_parent="{parent}"/>')
    return f"{u}.n"


def dec(parent, n_net):
    u = nu()
    e(f'  <Node _name="Number To Decimal String" inputs="number:{n_net}" outputs="decimal integer string:{u}.s" uid="{u}" uid_parent="{parent}"/>')
    return f"{u}.s"


def chain(loop, err_in):
    """A shift register that passes the run's error through an op loop untouched - the only thing it
    does is make the next loop and the save wait for this one."""
    sr, l_, r_ = nu(), nu(), nu()
    e(f'    <ShiftReg uid="{sr}" uid_parent="{loop}">')
    e(f'      <Left inputs="value:{err_in}" outputs="value:{l_}.err" uid="{l_}" uid_parent="{sr}"/>')
    e(f'      <Right inputs="value:{l_}.err" outputs="value:{r_}.err" uid="{r_}" uid_parent="{sr}"/>')
    e('    </ShiftReg>')
    return f"{r_}.err"


def code_of(parent, err_net):
    u = nu()
    e(f'  <Node _name="Unbundle By Name" fields="code" inputs="input cluster:{err_net}" outputs="code:{u}.code" uid="{u}" uid_parent="{parent}"/>')
    return dec(parent, f"{u}.code")


def concat(parent, nets):
    u = nu()
    ins = ",".join(f"string:{n}" for n in nets)
    e(f'  <Node _name="Concatenate Strings" inputs="{ins}" outputs="concatenated string:{u}.s" uid="{u}" uid_parent="{parent}"/>')
    return f"{u}.s"


desc = ("Applies a layout computed by lvai_layout_diagram to a VI's block diagram and saves it in place. "
        "Five passes, in this order\\3A MOVES, each 'path;index;left;top' - the object at that index of the "
        "diagram the path reaches is moved so that its Position becomes left,top in the pane's coordinates; "
        "SIZES, each 'path;index;width;height' - that structure's Frame Size is written, which sets its outer size exactly and keeps its top left corner (Auto Size and ShrinkWrapStructs grow a structure but never shrink it, measured 2026-10-16); TUNNELS, each 'path;index;tunnel;left;top' - that tunnel of that structure is moved; SHIFT REGISTERS, the same for a loop's Shift Registers[] (the left terminal; the right one follows); WIRES, each 'path' - every "
        "wire of that diagram is cleaned up with CleanUpWire. A path is 'object frame object frame ...' from "
        "the top-level diagram, empty for the top level itself, the ids lvbd_layout_dump writes.\\0A\\0A"
        "WHY EVERY OBJECT IS MOVED TWICE\\3A Position reads in the pane's coordinates while Move takes a "
        "position relative to the OWNER diagram (lvbd_graft_rewire, 2026-09-30). The first Move reveals the "
        "offset, the second applies target + target - landed.\\0A\\0A"
        "Each pass answers ONE string, a record per op ending in '|' and its fields split by ';' - the first field the error code. An object that fails is reported with its error code and the rest go on; only opening and saving "
        "the VI stop the run.")

e(f'<VI _name="lvbd_layout_apply.vi" description="{desc}">')
e('  <FreeLabel comment="Moves, sizes, tunnels, wires, then save" uid="4200" uid_parent="root"/>')
vi = nu()
e(f'  <Control _name="vi path" conIdx="0" connection="recommended" type="string" uid="{vi}" uid_parent="root" value="" outputs="value:{vi}.value"/>')
mv = nu()
e(f'  <Control _name="moves" conIdx="1" connection="recommended" type="array{{string}}" uid="{mv}" uid_parent="root" value="[]" outputs="value:{mv}.value"/>')
sz = nu()
e(f'  <Control _name="sizes" conIdx="2" connection="recommended" type="array{{string}}" uid="{sz}" uid_parent="root" value="[]" outputs="value:{sz}.value"/>')
wr = nu()
e(f'  <Control _name="wires" conIdx="3" connection="recommended" type="array{{string}}" uid="{wr}" uid_parent="root" value="[]" outputs="value:{wr}.value"/>')
srn = nu()
e(f'  <Control _name="shift registers" type="array{{string}}" uid="{srn}" uid_parent="root" value="[]" outputs="value:{srn}.value"/>')
tn = nu()
e(f'  <Control _name="tunnels" type="array{{string}}" uid="{tn}" uid_parent="root" value="[]" outputs="value:{tn}.value"/>')
sln = nu()
e(f'  <Control _name="selectors" type="array{{string}}" uid="{sln}" uid_parent="root" value="[]" outputs="value:{sln}.value"/>')
ei = nu()
e(f'  <Control _name="error in" conIdx="11" connection="recommended" type="cluster{{bool.status,int32.code,string.source}}" uid="{ei}" uid_parent="root" value="[false,0,]" outputs="value:{ei}.value"/>')
u = nu()
e(f'  <Node _name="String To Path" inputs="string:{vi}.value" outputs="path:{u}.path" uid="{u}" uid_parent="root"/>')
path = f"{u}.path"
u = nu()
e(f'  <Node _name="Open VI Reference" inputs="vi path:{path},error in (no error):{ei}.value" outputs="vi reference:{u}.vi,error out:{u}.err" uid="{u}" uid_parent="root"/>')
viref, err = f"{u}.vi", f"{u}.err"
u = nu()
e(f'  <Node _name="Property Node" fields="read+Block Diagram" type="{{LV.VI}}" inputs="reference:{viref},error in (no error):{err}" outputs="Block Diagram:{u}.bd,reference out:,error out:{u}.err" uid="{u}" uid_parent="root"/>')
bd, err = f"{u}.bd", f"{u}.err"
dcls = const("root", "diagram class", "ref{LV.Diagram}", "")
u = nu()
e(f'  <Node _name="To More Generic Class" inputs="reference:{bd},target class:{dcls}" outputs="generic class reference:{u}.top" uid="{u}" uid_parent="root"/>')
top = f"{u}.top"
run_err = err

# ---- moves
L = nu()
e(f'  <Structure _name="For Loop" count="" maxin="" maxout="" uid="{L}" uid_parent="root">')
t1, t2 = nu(), nu()
e(f'    <Tunnel _id="In1" inputs="value:{mv}.value" mode="index" outputs="value:{t1}.op" uid="{t1}" uid_parent="{L}"/>')
e(f'    <Tunnel _id="In2" inputs="value:{top}" outputs="value:{t2}.top" uid="{t2}" uid_parent="{L}"/>')
f = split(L, f"{t1}.op", 4)
d, err = walk(L, f[0], f"{t2}.top")
idx, x, y = number(L, f[1]), number(L, f[2]), number(L, f[3])
u = nu()
e(f'    <Node _name="Property Node" fields="read+All Objects[]" type="{{LV.Diagram}}" inputs="reference:{d},error in (no error):{err}" outputs="All Objects[]:{u}.objs,reference out:,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
objs, err = f"{u}.objs", f"{u}.err"
u = nu()
e(f'    <Node _name="Index Array" inputs="array:{objs},index:{idx}" outputs="element:{u}.obj" uid="{u}" uid_parent="{L}"/>')
obj = f"{u}.obj"
u = nu()
e(f'    <Node _name="Property Node" fields="read+Position" type="{{LV.GObject}}" inputs="reference:{obj},error in (no error):{err}" outputs="Position:{u}.pos,reference out:,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
pos, err = f"{u}.pos", f"{u}.err"
u = nu()
e(f'    <Node _name="Bundle By Name" fields="Left,Top" inputs="Left:{x},Top:{y},input cluster:{pos}" outputs="output cluster:{u}.target" uid="{u}" uid_parent="{L}"/>')
target = f"{u}.target"
dup = const(L, "duplicate", "bool", "false")
u = nu()
e(f'    <Node _name="Invoke Node" target="Move" type="{{LV.GObject}}" inputs="reference:{obj},error in (no error):{err},position:{target},owner:{d},duplicate:{dup}" outputs="reference out:,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
err = f"{u}.err"
u = nu()
e(f'    <Node _name="Property Node" fields="read+Position" type="{{LV.GObject}}" inputs="reference:{obj},error in (no error):{err}" outputs="Position:{u}.landed,reference out:,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
landed, err = f"{u}.landed", f"{u}.err"
u = nu()
e(f'    <Node _name="Add" inputs="x:{target},y:{target}" outputs="x+y:{u}.twice" uid="{u}" uid_parent="{L}"/>')
twice = f"{u}.twice"
u = nu()
e(f'    <Node _name="Subtract" inputs="x:{twice},y:{landed}" outputs="x-y:{u}.corrected" uid="{u}" uid_parent="{L}"/>')
corrected = f"{u}.corrected"
u = nu()
e(f'    <Node _name="Invoke Node" target="Move" type="{{LV.GObject}}" inputs="reference:{obj},error in (no error):{err},position:{corrected},owner:{d},duplicate:{dup}" outputs="reference out:,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
err = f"{u}.err"
u = nu()
e(f'    <Node _name="Property Node" fields="read+Position\\3ALeft,read+Position\\3ATop" type="{{LV.GObject}}" inputs="reference:{obj},error in (no error):{err}" outputs="Position\\3ALeft:{u}.l,Position\\3ATop:{u}.t,reference out:,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
fl, ft, err = f"{u}.l", f"{u}.t", f"{u}.err"
semi = const(L, "result separator", "string", ";")
line = concat(L, [code_of(L, err), semi, dec(L, fl), semi, dec(L, ft), const(L, "record end", "string", "|")])
mo = nu()
e(f'    <Tunnel _id="Out1" inputs="value:{line}" mode="index" outputs="value:{mo}.value" uid="{mo}" uid_parent="{L}"/>')
run_err = chain(L, run_err)
e('  </Structure>')

# ---- sizes
L = nu()
e(f'  <Structure _name="For Loop" count="" maxin="" maxout="" uid="{L}" uid_parent="root">')
t1, t2 = nu(), nu()
e(f'    <Tunnel _id="In1" inputs="value:{sz}.value" mode="index" outputs="value:{t1}.op" uid="{t1}" uid_parent="{L}"/>')
e(f'    <Tunnel _id="In2" inputs="value:{top}" outputs="value:{t2}.top" uid="{t2}" uid_parent="{L}"/>')
f = split(L, f"{t1}.op", 4)
d, err = walk(L, f[0], f"{t2}.top")
idx, w, h = number(L, f[1]), number(L, f[2]), number(L, f[3])
u = nu()
e(f'    <Node _name="Property Node" fields="read+All Objects[]" type="{{LV.Diagram}}" inputs="reference:{d},error in (no error):{err}" outputs="All Objects[]:{u}.objs,reference out:,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
objs, err = f"{u}.objs", f"{u}.err"
u = nu()
e(f'    <Node _name="Index Array" inputs="array:{objs},index:{idx}" outputs="element:{u}.obj" uid="{u}" uid_parent="{L}"/>')
obj = f"{u}.obj"
scls = const(L, "structure class", "ref{LV.Structure}", "")
u = nu()
e(f'    <Node _name="To More Specific Class" inputs="reference:{obj},error in:{err},target class:{scls}" outputs="specific class reference:{u}.st,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
st, err = f"{u}.st", f"{u}.err"
u = nu()
e(f'    <Node _name="Property Node" fields="read+Frame Size" type="{{LV.Structure}}" inputs="reference:{st},error in (no error):{err}" outputs="Frame Size:{u}.fs,reference out:,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
fs, err = f"{u}.fs", f"{u}.err"
u = nu()
e(f'    <Node _name="Bundle By Name" fields="Width,Height" inputs="Width:{w},Height:{h},input cluster:{fs}" outputs="output cluster:{u}.fs" uid="{u}" uid_parent="{L}"/>')
nfs = f"{u}.fs"
u = nu()
e(f'    <Node _name="Property Node" fields="write+Frame Size" type="{{LV.Structure}}" inputs="reference:{st},error in (no error):{err},Frame Size:{nfs}" outputs="reference out:,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
err = f"{u}.err"
u = nu()
e(f'    <Node _name="Property Node" fields="read+Position\\3ALeft,read+Position\\3ATop,read+Bounds\\3AArea Width,read+Bounds\\3AArea Height" type="{{LV.Structure}}" inputs="reference:{st},error in (no error):{err}" outputs="Position\\3ALeft:{u}.l,Position\\3ATop:{u}.t,Bounds\\3AArea Width:{u}.w,Bounds\\3AArea Height:{u}.h,reference out:,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
sl, stp, sw, sh, err = f"{u}.l", f"{u}.t", f"{u}.w", f"{u}.h", f"{u}.err"
semi = const(L, "result separator", "string", ";")
line = concat(L, [code_of(L, err), semi, dec(L, sl), semi, dec(L, stp), semi, dec(L, sw), semi, dec(L, sh), const(L, "record end", "string", "|")])
so = nu()
e(f'    <Tunnel _id="Out1" inputs="value:{line}" mode="index" outputs="value:{so}.value" uid="{so}" uid_parent="{L}"/>')
run_err = chain(L, run_err)
e('  </Structure>')

def border_pass(ops, cls_name, cls_type, prop, prop_type, indexed):
    """Moves a border object of a structure - a tunnel, a shift register or a case selector - with
    the double move and the owner UNWIRED: one op per string, 'path;object;index;x;y'."""
    global run_err
    L = nu()
    e(f'  <Structure _name="For Loop" count="" maxin="" maxout="" uid="{L}" uid_parent="root">')
    t1, t2 = nu(), nu()
    e(f'    <Tunnel _id="In1" inputs="value:{ops}.value" mode="index" outputs="value:{t1}.op" uid="{t1}" uid_parent="{L}"/>')
    e(f'    <Tunnel _id="In2" inputs="value:{top}" outputs="value:{t2}.top" uid="{t2}" uid_parent="{L}"/>')
    f = split(L, f"{t1}.op", 5)
    d, err = walk(L, f[0], f"{t2}.top")
    idx, ti, x, y = number(L, f[1]), number(L, f[2]), number(L, f[3]), number(L, f[4])
    u = nu()
    e(f'    <Node _name="Property Node" fields="read+All Objects[]" type="{{LV.Diagram}}" inputs="reference:{d},error in (no error):{err}" outputs="All Objects[]:{u}.objs,reference out:,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
    objs, err = f"{u}.objs", f"{u}.err"
    u = nu()
    e(f'    <Node _name="Index Array" inputs="array:{objs},index:{idx}" outputs="element:{u}.obj" uid="{u}" uid_parent="{L}"/>')
    obj = f"{u}.obj"
    scls = const(L, cls_name, cls_type, "")
    u = nu()
    e(f'    <Node _name="To More Specific Class" inputs="reference:{obj},error in:{err},target class:{scls}" outputs="specific class reference:{u}.st,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
    st, err = f"{u}.st", f"{u}.err"
    u = nu()
    e(f'    <Node _name="Property Node" fields="read+{prop}" type="{prop_type}" inputs="reference:{st},error in (no error):{err}" outputs="{prop}:{u}.tuns,reference out:,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
    tuns, err = f"{u}.tuns", f"{u}.err"
    if indexed:
        u = nu()
        e(f'    <Node _name="Index Array" inputs="array:{tuns},index:{ti}" outputs="element:{u}.tun" uid="{u}" uid_parent="{L}"/>')
        tun = f"{u}.tun"
    else:
        gcls = const(L, "object class", "ref{LV.GObject}", "")
        u = nu()
        e(f'    <Node _name="To More Generic Class" inputs="reference:{tuns},target class:{gcls}" outputs="generic class reference:{u}.tun" uid="{u}" uid_parent="{L}"/>')
        tun = f"{u}.tun"
    u = nu()
    e(f'    <Node _name="Property Node" fields="read+Position" type="{{LV.GObject}}" inputs="reference:{tun},error in (no error):{err}" outputs="Position:{u}.pos,reference out:,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
    pos, err = f"{u}.pos", f"{u}.err"
    u = nu()
    e(f'    <Node _name="Bundle By Name" fields="Left,Top" inputs="Left:{x},Top:{y},input cluster:{pos}" outputs="output cluster:{u}.target" uid="{u}" uid_parent="{L}"/>')
    target = f"{u}.target"
    dup = const(L, "duplicate", "bool", "false")
    u = nu()
    e(f'    <Node _name="Invoke Node" target="Move" type="{{LV.GObject}}" inputs="reference:{tun},error in (no error):{err},position:{target},owner:,duplicate:{dup}" outputs="reference out:,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
    err = f"{u}.err"
    u = nu()
    e(f'    <Node _name="Property Node" fields="read+Position" type="{{LV.GObject}}" inputs="reference:{tun},error in (no error):{err}" outputs="Position:{u}.landed,reference out:,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
    landed, err = f"{u}.landed", f"{u}.err"
    u = nu()
    e(f'    <Node _name="Add" inputs="x:{target},y:{target}" outputs="x+y:{u}.twice" uid="{u}" uid_parent="{L}"/>')
    twice = f"{u}.twice"
    u = nu()
    e(f'    <Node _name="Subtract" inputs="x:{twice},y:{landed}" outputs="x-y:{u}.corrected" uid="{u}" uid_parent="{L}"/>')
    corrected = f"{u}.corrected"
    u = nu()
    e(f'    <Node _name="Invoke Node" target="Move" type="{{LV.GObject}}" inputs="reference:{tun},error in (no error):{err},position:{corrected},owner:,duplicate:{dup}" outputs="reference out:,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
    err = f"{u}.err"
    u = nu()
    e(f'    <Node _name="Property Node" fields="read+Position\\3ALeft,read+Position\\3ATop" type="{{LV.GObject}}" inputs="reference:{tun},error in (no error):{err}" outputs="Position\\3ALeft:{u}.l,Position\\3ATop:{u}.t,reference out:,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
    fl, ft, err = f"{u}.l", f"{u}.t", f"{u}.err"
    semi = const(L, "result separator", "string", ";")
    line = concat(L, [code_of(L, err), semi, dec(L, fl), semi, dec(L, ft), const(L, "record end", "string", "|")])
    res = nu()
    e(f'    <Tunnel _id="Out1" inputs="value:{line}" mode="index" outputs="value:{res}.value" uid="{res}" uid_parent="{L}"/>')
    run_err = chain(L, run_err)
    e('  </Structure>')
    return res


# ---- tunnels, shift registers (Shift Registers[] answers the RIGHT register of each pair), and
# a case structure's selector, which Tunnels[] does not list
to = border_pass(tn, "structure class", "ref{LV.Structure}", "Tunnels[]", "{LV.Structure}", True)
sro = border_pass(srn, "loop class", "ref{LV.Loop}", "Shift Registers[]", "{LV.Loop}", True)
sel = border_pass(sln, "case structure class", "ref{LV.CaseStructure}", "Selector", "{LV.CaseStructure}", False)

# ---- wires
L = nu()
e(f'  <Structure _name="For Loop" count="" maxin="" maxout="" uid="{L}" uid_parent="root">')
t1, t2 = nu(), nu()
e(f'    <Tunnel _id="In1" inputs="value:{wr}.value" mode="index" outputs="value:{t1}.op" uid="{t1}" uid_parent="{L}"/>')
e(f'    <Tunnel _id="In2" inputs="value:{top}" outputs="value:{t2}.top" uid="{t2}" uid_parent="{L}"/>')
d, err = walk(L, f"{t1}.op", f"{t2}.top")
u = nu()
e(f'    <Node _name="Property Node" fields="read+Wires[]" type="{{LV.Diagram}}" inputs="reference:{d},error in (no error):{err}" outputs="Wires[]:{u}.wires,reference out:,error out:{u}.err" uid="{u}" uid_parent="{L}"/>')
wires, err = f"{u}.wires", f"{u}.err"
W = nu()
e(f'    <Structure _name="For Loop" count="" maxin="" maxout="" uid="{W}" uid_parent="{L}">')
tw = nu()
e(f'      <Tunnel _id="In1" inputs="value:{wires}" mode="index" outputs="value:{tw}.wire" uid="{tw}" uid_parent="{W}"/>')
u = nu()
e(f'      <Node _name="Invoke Node" target="CleanUpWire" type="{{LV.Wire}}" inputs="reference:{tw}.wire,error in (no error):" outputs="reference out:,error out:{u}.err" uid="{u}" uid_parent="{W}"/>')
werr = f"{u}.err"
u = nu()
e(f'      <Node _name="Unbundle By Name" fields="status" inputs="input cluster:{werr}" outputs="status:{u}.bad" uid="{u}" uid_parent="{W}"/>')
wo = nu()
e(f'      <Tunnel _id="Out1" inputs="value:{u}.bad" mode="index" outputs="value:{wo}.value" uid="{wo}" uid_parent="{W}"/>')
e('    </Structure>')
u = nu()
e(f'    <Node _name="Array Size" inputs="array:{wires}" outputs="size(s):{u}.n" uid="{u}" uid_parent="{L}"/>')
n = f"{u}.n"
semi = const(L, "result separator", "string", ";")
line = concat(L, [code_of(L, err), semi, dec(L, n), const(L, "record end", "string", "|")])
wo2 = nu()
e(f'    <Tunnel _id="Out1" inputs="value:{line}" mode="index" outputs="value:{wo2}.value" uid="{wo2}" uid_parent="{L}"/>')
run_err = chain(L, run_err)
e('  </Structure>')

u = nu()
e(f'  <Node _name="Invoke Node" target="Save.Instrument" type="{{LV.VI}}" inputs="reference:{viref},error in (no error):{run_err},Path to saved file:,Save a Copy:,Without Diagram:" outputs="reference out:,error out:{u}.err" uid="{u}" uid_parent="root"/>')
serr = f"{u}.err"
u = nu()
e(f'  <Node _name="Close Reference" inputs="reference:{viref},error in (no error):{serr}" outputs="error out:{u}.err" uid="{u}" uid_parent="root"/>')
cerr = f"{u}.err"
j = nu()
e(f'  <Node _name="Concatenate Strings" inputs="string:{mo}.value" outputs="concatenated string:{j}.s" uid="{j}" uid_parent="root"/>')
u = nu()
e(f'  <Indicator _name="move results" conIdx="4" connection="recommended" type="string" uid="{u}" uid_parent="root" value="" inputs="value:{j}.s"/>')
j = nu()
e(f'  <Node _name="Concatenate Strings" inputs="string:{so}.value" outputs="concatenated string:{j}.s" uid="{j}" uid_parent="root"/>')
u = nu()
e(f'  <Indicator _name="size results" conIdx="5" connection="recommended" type="string" uid="{u}" uid_parent="root" value="" inputs="value:{j}.s"/>')
j = nu()
e(f'  <Node _name="Concatenate Strings" inputs="string:{to}.value" outputs="concatenated string:{j}.s" uid="{j}" uid_parent="root"/>')
u = nu()
e(f'  <Indicator _name="tunnel results" conIdx="7" connection="recommended" type="string" uid="{u}" uid_parent="root" value="" inputs="value:{j}.s"/>')
j = nu()
e(f'  <Node _name="Concatenate Strings" inputs="string:{sro}.value" outputs="concatenated string:{j}.s" uid="{j}" uid_parent="root"/>')
u = nu()
e(f'  <Indicator _name="shift register results" type="string" uid="{u}" uid_parent="root" value="" inputs="value:{j}.s"/>')
j = nu()
e(f'  <Node _name="Concatenate Strings" inputs="string:{sel}.value" outputs="concatenated string:{j}.s" uid="{j}" uid_parent="root"/>')
u = nu()
e(f'  <Indicator _name="selector results" type="string" uid="{u}" uid_parent="root" value="" inputs="value:{j}.s"/>')
j = nu()
e(f'  <Node _name="Concatenate Strings" inputs="string:{wo2}.value" outputs="concatenated string:{j}.s" uid="{j}" uid_parent="root"/>')
u = nu()
e(f'  <Indicator _name="wire results" conIdx="6" connection="recommended" type="string" uid="{u}" uid_parent="root" value="" inputs="value:{j}.s"/>')
u = nu()
e(f'  <Indicator _name="error out" conIdx="15" connection="recommended" type="cluster{{bool.status,int32.code,string.source}}" uid="{u}" uid_parent="root" value="[false,0,]" inputs="value:{cerr}"/>')
e('</VI>')

open(sys.argv[1], "w", encoding="utf-8", newline="\n").write("\n".join(out) + "\n")
print("uids up to", uid[0])
