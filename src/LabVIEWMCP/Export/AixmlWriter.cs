using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace LabVIEWMcp.Export;

/// Turns `scripts/lvdiag_probe_v16.xml`'s records into AIXML.
///
/// The probe deliberately does no interpretation: it reports LabVIEW's own class names,
/// labels and terminal names, identifies everything by a Type Cast integer, and hands the
/// front panel over as LabVIEW's own flattened XML. All the mapping lives here, where it is
/// testable without LabVIEW in the loop.
internal static class AixmlWriter
{
    internal sealed record Obj(long Diagram, long Id, string Cls, string Label);
    internal sealed record Term(long Net, long TermId, long OwnerId, string Name, bool IsSource);
    internal sealed record Boundary(long StructureId, long Id, string Cls);
    /// A front-panel object as LabVIEW reports it: AIXML type, AIXML value literal, and
    /// which of the two Ctrl Val.Get All passes found it.
    internal sealed record FrontPanel(string Type, string Value, bool IsControl, string RawType);

    internal sealed record Model(
        List<Obj> Objects, List<Term> Terminals, List<Boundary> Boundaries,
        Dictionary<string, FrontPanel> Fp, Dictionary<long, FrontPanel> ConstVals,
        Dictionary<string, int> ConIdx, string ViDescription,
        Dictionary<long, string> ObjDescription, long TopDiagram);

    // ---------- parsing ----------

    public static Model Parse(string text)
    {
        var objs = new List<Obj>();
        var terms = new List<Term>();
        var bounds = new List<Boundary>();
        var fp = new Dictionary<string, FrontPanel>(StringComparer.Ordinal);

        var (records, ctlXml, indXml, constText, paneText, descText, objDescText) = SplitSections(text);

        foreach (var raw in records)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith("B/"))
            {
                var p = line[2..].Split('/', 3);
                if (p.Length == 3 && long.TryParse(p[0], out var s) && long.TryParse(p[1], out var id))
                    bounds.Add(new Boundary(s, id, p[2]));
            }
            else if (line.StartsWith("N/"))
            {
                var body = line[2..];
                var lastSlash = body.LastIndexOf('/');
                if (lastSlash < 0) continue;
                var dir = body[(lastSlash + 1)..];
                var head = body[..lastSlash].Split('/', 4);
                if (head.Length == 4
                    && long.TryParse(head[0], out var net)
                    && long.TryParse(head[1], out var tid)
                    && long.TryParse(head[2], out var oid))
                    terms.Add(new Term(net, tid, oid, head[3], dir == "SRC"));
            }
            else
            {
                var p = line.Split('/', 4);
                if (p.Length >= 3
                    && long.TryParse(p[0], out var d)
                    && long.TryParse(p[1], out var id))
                    objs.Add(new Obj(d, id, p[2], p.Length > 3 ? p[3] : ""));
            }
        }

        foreach (var (xml, isControl) in new[] { (ctlXml, true), (indXml, false) })
            foreach (var e in ParseFlattened(xml, isControl))
                fp[e.Key] = e.Value;

        // One blob per object, whether or not it is a constant - the downcast that feeds it
        // fails silently elsewhere and yields an empty variant. Keep the ones whose class we
        // already know to be a constant; the rest carry <Default> and are dropped here.
        var constVals = new Dictionary<long, FrontPanel>();
        foreach (var (id, blob) in SplitMarked(constText, "#CONST "))
        {
            var fpv = ParseVariant(blob);
            if (fpv is not null) constVals[id] = fpv;
        }

        // Connector Pane Controls[] comes back in PANE order, so the line index is the
        // conIdx. Blank lines are pane terminals with nothing assigned and are skipped
        // without disturbing the numbering.
        var conIdx = new Dictionary<string, int>(StringComparer.Ordinal);
        var paneLines = paneText.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        for (var i = 0; i < paneLines.Length; i++)
        {
            var nm = paneLines[i].Trim();
            if (nm.Length > 0) conIdx[nm] = i;
        }

        // Same marker-delimited shape as the constant blobs, because a description is free
        // text and may run to several lines.
        var objDesc = new Dictionary<long, string>();
        foreach (var (id, body) in SplitMarked(objDescText, "#DESC "))
        {
            var d = body.Trim('\r', '\n');
            if (d.Length > 0) objDesc[id] = d;
        }

        var top = objs.Count > 0 ? objs[0].Diagram : 0;
        return new Model(objs, terms, bounds, fp, constVals, conIdx,
            descText.Trim('\r', '\n'), objDesc, top);
    }

    private static (List<string> Records, string Ctl, string Ind, string Const, string Pane, string Desc, string ObjDesc) SplitSections(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var records = new List<string>();
        var ctl = new StringBuilder();
        var ind = new StringBuilder();
        var cst = new StringBuilder();
        var pane = new StringBuilder();
        var desc = new StringBuilder();
        var odesc = new StringBuilder();
        var into = 0; // 0 records, 1 controls, 2 indicators, 3 constants, 4 pane, 5 VI description, 6 object descriptions
        foreach (var line in lines)
        {
            if (line.StartsWith("#FPCONTROLS")) { into = 1; continue; }
            if (line.StartsWith("#FPINDICATORS")) { into = 2; continue; }
            if (line.StartsWith("#CONSTVALUES")) { into = 3; continue; }
            if (line.StartsWith("#CONPANE")) { into = 4; continue; }
            if (line.StartsWith("#VIDESC")) { into = 5; continue; }
            if (line.StartsWith("#OBJDESC")) { into = 6; continue; }
            switch (into)
            {
                case 0: records.Add(line); break;
                case 1: ctl.AppendLine(line); break;
                case 2: ind.AppendLine(line); break;
                case 3: cst.AppendLine(line); break;
                case 4: pane.AppendLine(line); break;
                case 5: desc.AppendLine(line); break;
                default: odesc.AppendLine(line); break;
            }
        }
        return (records, ctl.ToString(), ind.ToString(), cst.ToString(),
                pane.ToString(), desc.ToString(), odesc.ToString());
    }

    /// Marker-delimited blocks keyed by object id. Both the constant blobs and the object
    /// descriptions use this shape, because both are free text that may run to several lines
    /// and so cannot share the one-record-per-line format.
    private static IEnumerable<(long Id, string Body)> SplitMarked(string text, string marker)
    {
        long? id = null;
        var sb = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith(marker))
            {
                if (id is { } prev) yield return (prev, sb.ToString());
                sb.Clear();
                id = long.TryParse(line[marker.Length..].Trim(), out var v) ? v : null;
            }
            else if (id is not null) sb.AppendLine(line);
        }
        if (id is { } last) yield return (last, sb.ToString());
    }

    /// A bare LvVariant wrapper, as a constant's Value arrives. Null when it holds nothing -
    /// which is what a failed downcast produces, and is how non-constants are filtered out.
    private static FrontPanel? ParseVariant(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        XElement root;
        try { root = XElement.Parse(xml); } catch { return null; }
        var data = root.Elements().FirstOrDefault(e => e.Name.LocalName is not ("Name" or "Default"));
        if (data is null) return null;
        var raw = data.Name.LocalName;
        return new FrontPanel(AixmlType(raw), AixmlValue(raw, data), false, raw);
    }

    /// LabVIEW's Flatten To XML shape:
    ///   Array > Dimsize, then one Cluster per object holding String(Name) and LvVariant(data)
    ///
    /// Dimsize is authoritative. An EMPTY array still carries one child element describing the
    /// element type, so counting Clusters reports a phantom object for a VI with no controls -
    /// which is not hypothetical: a VI whose only front-panel object is an indicator hits it
    /// on the controls pass.
    private static IEnumerable<KeyValuePair<string, FrontPanel>> ParseFlattened(string xml, bool isControl)
    {
        if (string.IsNullOrWhiteSpace(xml)) yield break;
        XElement root;
        try { root = XElement.Parse(xml); } catch { yield break; }

        var dimsize = (int?)root.Element("Dimsize") ?? 0;
        if (dimsize == 0) yield break;

        foreach (var cluster in root.Elements("Cluster").Take(dimsize))
        {
            var name = cluster.Elements("String")
                .FirstOrDefault(s => (string?)s.Element("Name") == "Name")
                ?.Element("Val")?.Value;
            if (string.IsNullOrEmpty(name)) continue;

            var data = cluster.Element("LvVariant")?
                .Elements().FirstOrDefault(e => e.Name.LocalName != "Name");
            if (data is null) continue;

            var raw = data.Name.LocalName;
            yield return new KeyValuePair<string, FrontPanel>(
                name, new FrontPanel(AixmlType(raw), AixmlValue(raw, data), isControl, raw));
        }
    }

    /// LabVIEW's flattened element name -> the type grammar of section 5.
    private static string AixmlType(string lv) => lv switch
    {
        "DBL" => "double",
        "String" => "string",
        "Boolean" => "bool",
        "I32" => "int32",
        "U32" => "uint32",
        "U8" => "uint8",
        _ => "",   // composites and the numeric long tail - reported as a gap, never guessed
    };

    private static string AixmlValue(string lv, XElement data)
    {
        var val = data.Element("Val")?.Value ?? "";
        return lv switch
        {
            // 3.00000000000000 -> 3, which is how NI writes it
            "DBL" when double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                => d.ToString("R", CultureInfo.InvariantCulture),
            "Boolean" => val == "1" ? "true" : "false",
            _ => val,
        };
    }


    /// AIXML's backslash-hex layer, which stacks on top of XML entities (XElement does those).
    /// Colon and comma are the separators inside inputs/outputs, so section 6 escapes them
    /// EVERYWHERE, free text included - NI's own export writes
    /// "Adds two numbers\3A C = A + B\2C so ...".
    ///
    /// Only the four an export actually emits. The decoder accepts the general \XX form, but
    /// emitting more than NI does would make authored and exported files differ, which is the
    /// thing this is for. A literal backslash is left alone: \5C is attested inside a value=
    /// payload, not in free text, so escaping it here is unverified.
    private static string Esc(string s) => s
        .Replace(":", "\\3A")
        .Replace(",", "\\2C")
        .Replace("\r", "\\0D")
        .Replace("\n", "\\0A");
    // ---------- class -> AIXML element kind ----------

    private enum Kind { Node, Control, Constant, FreeLabel, Structure, Unsupported }

    private static Kind KindOf(string cls) => cls switch
    {
        "ControlTerminal" => Kind.Control,
        "Text" => Kind.FreeLabel,
        var c when c.EndsWith("Constant") => Kind.Constant,
        "ForLoop" or "WhileLoop" or "CaseStructure" or "EventStructure"
            or "FlatSequence" or "Sequence" or "TimedLoop" => Kind.Structure,
        "LoopTunnel" or "LeftShiftRegister" or "RightShiftRegister" or "Terminal" => Kind.Unsupported,
        _ => Kind.Node,
    };

    // ---------- serialisation ----------

    public sealed record Result(string Xml, List<string> Gaps);

    public static Result ToAixml(Model m, string viName, string description)
    {
        var gaps = new List<string>();

        var objs = m.Objects.Where(o => o.Diagram == m.TopDiagram).ToList();
        var byId = objs.ToDictionary(o => o.Id);

        var uid = new Dictionary<long, int>();
        var next = 10;
        foreach (var o in objs) { uid[o.Id] = next; next += 10; }

        Obj? Owner(Term t) =>
            byId.TryGetValue(t.OwnerId, out var byOwner) ? byOwner
            : byId.TryGetValue(t.TermId, out var bySelf) ? bySelf
            : null;

        static string TermName(Obj o, Term t) =>
            KindOf(o.Cls) is Kind.Control or Kind.Constant ? "value" : t.Name;

        var netName = new Dictionary<long, string>();
        foreach (var g in m.Terminals.GroupBy(t => t.Net))
        {
            var src = g.FirstOrDefault(t => t.IsSource);
            if (src is null) { gaps.Add($"net {g.Key}: no source terminal"); continue; }
            var owner = Owner(src);
            if (owner is null) { gaps.Add($"net {g.Key}: source owner {src.OwnerId} is off the top diagram"); continue; }
            netName[g.Key] = $"{uid[owner.Id]}.{TermName(owner, src)}";
        }

        var perObject = new Dictionary<long, List<Term>>();
        foreach (var t in m.Terminals)
        {
            var o = Owner(t);
            if (o is null) continue;
            (perObject.TryGetValue(o.Id, out var l) ? l : perObject[o.Id] = new()).Add(t);
        }

        string Wires(Obj o, bool sources)
        {
            if (!perObject.TryGetValue(o.Id, out var ts)) return "";
            return string.Join(",", ts
                .Where(t => t.IsSource == sources && netName.ContainsKey(t.Net))
                .Select(t => $"{TermName(o, t)}:{netName[t.Net]}"));
        }

        var vi = new XElement("VI",
            new XAttribute("_name", viName),
            new XAttribute("description", Esc(m.ViDescription.Length > 0 ? m.ViDescription : description)));
        foreach (var o in objs)
        {
            var u = uid[o.Id];
            var ins = Wires(o, sources: false);
            var outs = Wires(o, sources: true);

            switch (KindOf(o.Cls))
            {
                case Kind.Control:
                {
                    var t = perObject.TryGetValue(o.Id, out var l) ? l.FirstOrDefault() : null;
                    if (t is null) { gaps.Add($"object {o.Id} ({o.Cls}): unwired, skipped"); continue; }

                    // LabVIEW's own answer to control-vs-indicator, from which Ctrl Val.Get All
                    // pass found it. Wire direction is the fallback when it reported neither.
                    var known = m.Fp.TryGetValue(t.Name, out var f);
                    var isControl = known ? f!.IsControl : t.IsSource;
                    var type = known && f!.Type.Length > 0 ? f.Type : "";
                    if (!known) gaps.Add($"'{t.Name}': not reported by Ctrl Val.Get All - type and value unknown");
                    else if (type.Length == 0) gaps.Add($"'{t.Name}': LabVIEW type '{f!.RawType}' has no scalar AIXML spelling");

                    if (!m.ConIdx.TryGetValue(t.Name, out var idx))
                        gaps.Add($"'{t.Name}': not on the connector pane - conIdx omitted");
                    // connection is still inferred: GetWiringRule(TermIdx) would settle it and
                    // is not read yet, so it stays out of the comparison.
                    var e = new XElement(isControl ? "Control" : "Indicator",
                        new XAttribute("_name", Esc(t.Name)),
                        new XAttribute("connection", isControl ? "required" : "recommended"),
                        new XAttribute("type", type.Length > 0 ? type : "string"),
                        new XAttribute("uid", u),
                        new XAttribute("uid_parent", "root"),
                        new XAttribute("value", known ? Esc(f!.Value) : ""));
                    if (m.ConIdx.ContainsKey(t.Name)) e.SetAttributeValue("conIdx", m.ConIdx[t.Name]);
                    if (m.ObjDescription.TryGetValue(o.Id, out var od)) e.SetAttributeValue("description", Esc(od));
                    if (isControl) e.SetAttributeValue("outputs", outs); else e.SetAttributeValue("inputs", ins);
                    vi.Add(e);
                    break;
                }
                case Kind.Constant:
                {
                    var have = m.ConstVals.TryGetValue(o.Id, out var cv);
                    if (!have) gaps.Add($"object {o.Id}: no constant value reported");
                    else if (cv!.Type.Length == 0)
                        gaps.Add($"object {o.Id}: LabVIEW type '{cv.RawType}' has no scalar AIXML spelling");
                    vi.Add(new XElement("Constant",
                        new XAttribute("_name", Esc(o.Label)),
                        new XAttribute("outputs", outs),
                        new XAttribute("type", have && cv!.Type.Length > 0 ? cv.Type : "string"),
                        new XAttribute("uid", u),
                        new XAttribute("uid_parent", "root"),
                        new XAttribute("value", have ? Esc(cv!.Value) : "")));
                    break;
                }
                case Kind.FreeLabel:
                    gaps.Add($"object {o.Id}: FreeLabel text unread");
                    vi.Add(new XElement("FreeLabel",
                        new XAttribute("comment", Esc(o.Label)),
                        new XAttribute("uid", u),
                        new XAttribute("uid_parent", "root")));
                    break;
                case Kind.Structure:
                    gaps.Add($"object {o.Id} ({o.Cls}): structures are not serialised yet");
                    break;
                case Kind.Unsupported:
                    break;
                default:
                {
                    var e = new XElement("Node", new XAttribute("_name", Esc(o.Label)));
                    if (m.ObjDescription.TryGetValue(o.Id, out var nd)) e.SetAttributeValue("description", Esc(nd));
                    if (ins.Length > 0) e.SetAttributeValue("inputs", ins);
                    if (outs.Length > 0) e.SetAttributeValue("outputs", outs);
                    e.SetAttributeValue("uid", u);
                    e.SetAttributeValue("uid_parent", "root");
                    vi.Add(e);
                    break;
                }
            }
        }

        var sb = new StringBuilder();
        sb.Append(vi.ToString(SaveOptions.None));
        sb.AppendLine();
        return new Result(sb.ToString(), gaps);
    }

    // ---------- comparison ----------

    /// Topology: element kinds, names, and the wiring graph after uid normalisation.
    public static List<string> CompareTopology(string ourXml, string niXml)
        => Compare(ourXml, niXml, withAttributes: false);

    /// Topology plus the attributes we now claim to extract. Anything not claimed stays out
    /// of the comparison rather than being silently counted as agreement.
    public static List<string> CompareWithTypes(string ourXml, string niXml)
        => Compare(ourXml, niXml, withAttributes: true);

    private static List<string> Compare(string ourXml, string niXml, bool withAttributes)
    {
        var diffs = new List<string>();
        var a = Canonical(XElement.Parse(ourXml), withAttributes);
        var b = Canonical(XElement.Parse(niXml), withAttributes);

        foreach (var key in a.Keys.Union(b.Keys).OrderBy(k => k))
        {
            var inA = a.TryGetValue(key, out var va);
            var inB = b.TryGetValue(key, out var vb);
            if (!inA) { diffs.Add($"only NI has:   {key}"); continue; }
            if (!inB) { diffs.Add($"only ours has: {key}"); continue; }
            if (va != vb) diffs.Add($"differs {key}\n     ours: {va}\n       NI: {vb}");
        }
        return diffs;
    }

    private static Dictionary<string, string> Canonical(XElement vi, bool withAttributes)
    {
        var label = new Dictionary<string, string>();
        foreach (var e in vi.Elements())
        {
            var u = (string?)e.Attribute("uid");
            if (u is null) continue;
            label[u] = $"{e.Name.LocalName}:{(string?)e.Attribute("_name") ?? ""}";
        }

        string Rewrite(string net)
        {
            var dot = net.IndexOf('.');
            if (dot < 0) return net;
            var u = net[..dot];
            return label.TryGetValue(u, out var l) ? $"{l}.{net[(dot + 1)..]}" : net;
        }

        var result = new Dictionary<string, string>();
        foreach (var e in vi.Elements())
        {
            var key = $"{e.Name.LocalName}:{(string?)e.Attribute("_name") ?? ""}";
            var parts = new List<string>();
            foreach (var attr in new[] { "inputs", "outputs" })
            {
                var v = (string?)e.Attribute(attr);
                if (string.IsNullOrEmpty(v)) continue;
                foreach (var pair in v.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var colon = pair.IndexOf(':');
                    if (colon < 0) continue;
                    var net = pair[(colon + 1)..];
                    if (net.Length == 0) continue;
                    parts.Add($"{attr[..2]} {pair[..colon]} = {Rewrite(net)}");
                }
            }
            if (withAttributes)
                foreach (var attr in new[] { "type", "value", "conIdx", "description" })
                {
                    var v = (string?)e.Attribute(attr);
                    if (v is not null) parts.Add($"@{attr} = {v}");
                }
            parts.Sort(StringComparer.Ordinal);
            result[result.ContainsKey(key) ? key + "#2" : key] = string.Join(" | ", parts);
        }
        return result;
    }
}

