using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Reading a `.ctl` - what kind of control it is, and whether there is anything to bind to.
///
/// WHY THIS IS A TOOL. Measured 2026-09-02 across two class-generation runs: settling "is this
/// `.ctl` actually a typedef, and what type does it wrap?" cost about 90 s of wall clock against
/// 1.1 s of anything doing work - two <c>pylv_extract</c> calls plus four greps over an XML file
/// nobody wants to read. It is the cheapest question in the whole class workflow and it was the
/// worst latency ratio of the run, 140 : 1.
///
/// AND THE ANSWER MATTERS MORE THAN IT LOOKS. That run bound two fields against
/// <c>DAQmx Task Name NI_Silver.ctl</c> and <c>errclust.llb\Error Cluster.ctl</c>; both
/// <c>Replace</c> calls answered <c>error out = 0</c>, both installed the right type, and NEITHER
/// produced a typedef link - because neither file is a typedef. NI ships them as ordinary
/// controls. Nothing in the binding chain says so: the failure is indistinguishable from success
/// unless the source is checked first, which is exactly what this tool is for.
///
/// NO LABVIEW. Everything here is read off the file with pylabview, so it works with the IDE shut,
/// in CI, and while a project holds the class. <c>{LV.VI} Control VI Type</c> answers the same
/// question - <c>scripts/lvctl_kind.xml</c> drives it - but it needs a running LabVIEW and a round
/// trip, and the file already knows.
///
/// THE FOUR KINDS ARE ONE ENUM, and it is not a boolean. <c>Is Typedef?</c> is
/// <c>uint32{not a typedef, typedef, strict typedef, class private data}</c>, and this tool
/// reconstructs that number from three independent flags in the saved file rather than reporting
/// three booleans the caller has to combine.
/// </summary>
[McpServerToolType]
internal sealed class CtlTools
{
    /// <summary>The names LabVIEW's own `Control VI Type` enum uses, in its own order.</summary>
    internal static readonly string[] ControlViTypeNames =
        ["not a typedef", "typedef", "strict typedef", "class private data"];

    [McpServerTool(Name = "lvai_describe_ctl", ReadOnly = true,
                   Title = "What kind of control a .ctl is, and what type it wraps")]
    [Description("""
        READ-ONLY and NEEDS NO RUNNING LABVIEW: what kind of control a `.ctl` is, what type it
        wraps, and whether it can be bound to as a typedef.
        ASK THIS BEFORE BINDING ANYTHING. A `.ctl` that is not a typedef binds with `error out = 0`
        and produces NO typedef link - measured 2026-09-02 on two of NI's own controls, where both
        Replace calls succeeded and neither bound, because `DAQmx Task Name NI_Silver.ctl` and
        `errclust.llb\Error Cluster.ctl` are ordinary controls (`TypeDefVI="0"`). The success and
        the failure look identical from the calling side; only the source tells them apart.
        `controlVIType` is LabVIEW's own enum, reconstructed from the saved file rather than from a
        running IDE: 0 not a typedef, 1 typedef, 2 strict typedef, 3 class private data. It agrees
        with `{LV.VI} Control VI Type`, which `scripts/lvctl_kind.xml` reads the slow way.
        `bindable` is the verdict to act on, and `whyNotBindable` names the reason in one sentence.
        `wrappedType` is what the control actually carries - the type a binding would install, and
        the type a generated constant must be authored as.
        A `.vi` is accepted too and reports `isControl: false`, because pointing this at the wrong
        file is a likelier mistake than wanting it.
        """)]
    public Task<string> DescribeCtlAsync(
        [Description(@"Absolute path to the .ctl (a .vi is accepted and reported as not a control)")]
        string ctlPath,
        [Description("""
            Keep the pylabview bundle instead of deleting it. The bundle is an implementation
            detail of this call; pass true only when the raw XML is wanted for something else.
            """)]
        bool keepBundle = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 45,
        CancellationToken ct = default) =>
        Rpc.GuardAsync(async () =>
        {
            var bundle = PyLabview.Locate();
            if (bundle is null) return Json.Error("notProvisioned", PyLabview.NotProvisionedMessage());
            if (!File.Exists(ctlPath))
                return Json.Error("badArguments", $"No file at ctlPath '{ctlPath}'.");

            var extension = Path.GetExtension(ctlPath);
            if (extension is ".lvclass" or ".lvlib" or ".lvproj")
                return Json.Error("notAnRsrcFile",
                    $"'{extension}' files are plain XML in LabVIEW 2026, not RSRC containers. A " +
                    "class's PRIVATE DATA control lives inside the .lvclass as " +
                    "NI.LVClass.FlattenedPrivateDataCTL - unwrap that and read it, or call " +
                    "lvai_describe_class.");

            var total = Stopwatch.StartNew();
            var outDirectory = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "ctl",
                Path.GetRandomFileName());
            Directory.CreateDirectory(outDirectory);
            try
            {
                var mainXml = Path.Combine(outDirectory,
                    Path.GetFileNameWithoutExtension(ctlPath).Replace(" ", "") + ".xml");
                var budget = Rpc.ClampToolWait(timeoutSeconds);
                var extract = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                    ["-x", "-i", ctlPath, "-m", mainXml], budget, ct);
                if (extract.ExitCode != 0 || !File.Exists(mainXml))
                    return Json.Error("extractFailed",
                        $"pylabview exited {extract.ExitCode} and wrote no XML for '{ctlPath}'.",
                        new { stderr = extract.StdErr });

                var answer = Describe(XDocument.Load(mainXml).Root!, ctlPath);
                answer["elapsedMs"] = total.ElapsedMilliseconds;
                answer["bundleDirectory"] = keepBundle ? outDirectory : null;
                return Json.Document(answer);
            }
            finally
            {
                if (!keepBundle)
                    try { Directory.Delete(outDirectory, recursive: true); } catch { /* best effort */ }
            }
        });

    // ------------------------------------------------------------------ reading the file

    /// <summary>
    /// The whole verdict, from the extracted RSRC XML.
    ///
    /// THREE FLAGS, ONE ENUM. <c>TypeDefVI</c> and <c>StrictTypeDefVI</c> sit on the save record's
    /// <c>Execution</c> element; the private-data marker is <c>IsPrivateDataForUDClass</c> on
    /// <c>Execution2</c>, one element further down. Reading only the first two calls a class
    /// private data control "not a typedef", which is true of the flag and wrong as an answer.
    /// </summary>
    internal static JsonObject Describe(XElement rsrc, string ctlPath)
    {
        var section = rsrc.Element("LVSR")?.Element("Section");
        var execution = section?.Element("Execution");
        var execution2 = section?.Element("Execution2");
        var instrument = section?.Element("Instrument");

        var isTypedef = Flag(execution, "TypeDefVI");
        var isStrict = Flag(execution, "StrictTypeDefVI");
        var isPrivateData = Flag(execution2, "IsPrivateDataForUDClass");
        var instrumentType = (string?)instrument?.Attribute("Type");
        var isControl = instrumentType is "Control";

        // LabVIEW's own enum, in LabVIEW's own order. Private data wins over the other two: a
        // private data control carries StrictTypeDefVI as well, and calling it a strict typedef
        // would send a caller to Replace, which answers Error 1073 on one.
        var kind = isPrivateData ? 3 : isStrict ? 2 : isTypedef ? 1 : 0;

        var wrapped = WrappedType(rsrc);

        string? whyNot = null;
        if (!isControl)
            whyNot = $"This is not a control - its Instrument Type is '{instrumentType ?? "unknown"}'. " +
                     "Only a .ctl can be a typedef.";
        else if (kind == 3)
            whyNot = "This is a class PRIVATE DATA control. {LV.Control} Replace is refused on one " +
                     "with Error 1073 - edit an exported copy instead (scripts/lvpdc_*.xml, or " +
                     "lvai_bind_class_fields).";
        else if (kind == 0)
            whyNot = "This control is not a typedef (TypeDefVI=\"0\"), so there is nothing to bind " +
                     "to. A Replace against it still succeeds and still installs the type - it " +
                     "just produces no typedef link, which is indistinguishable from success " +
                     "unless you check here first.";

        return new JsonObject
        {
            ["ok"] = true,
            ["ctlPath"] = Path.GetFullPath(ctlPath),
            ["isControl"] = isControl,
            ["instrumentType"] = instrumentType,
            ["controlVIType"] = kind,
            ["controlVITypeName"] = ControlViTypeNames[kind],
            ["isTypedef"] = kind is 1 or 2,
            ["isStrictTypedef"] = kind == 2,
            ["isClassPrivateData"] = kind == 3,
            ["controlLabel"] = wrapped.Label,
            ["wrappedType"] = wrapped.Kind,
            ["wrappedTypeDetail"] = wrapped.Detail,
            ["fields"] = wrapped.Fields,
            ["bindable"] = whyNot is null,
            ["whyNotBindable"] = whyNot,
            ["source"] = "the saved file, read with pylabview - no LabVIEW was involved",
            ["note"] = "controlVIType matches {LV.VI} Control VI Type. Verify a binding from the " +
                       "TARGET afterwards: a bound field is a <TypeDesc Type=\"TypeDef\"> naming " +
                       "the .ctl, and this call answers what the SOURCE offers, not what took.",
        };
    }

    private static bool Flag(XElement? element, string name) =>
        (string?)element?.Attribute(name) == "1";

    private sealed record Wrapped(string? Label, string? Kind, string? Detail, JsonArray Fields);

    /// <summary>
    /// The type the control carries: <c>VCTP/TopLevel</c> index 1, resolved to its flat descriptor.
    ///
    /// INDEX 1, NOT 0. The top-level list is 1-based and its first entry is the control's own type;
    /// a cluster control's fields are that descriptor's children, which is what makes the field
    /// list readable without decoding the front panel heap.
    /// </summary>
    private static Wrapped WrappedType(XElement rsrc)
    {
        var vctp = rsrc.Element("VCTP")?.Element("Section");
        if (vctp is null) return new Wrapped(null, null, null, []);

        var flat = vctp.Elements("TypeDesc").ToList();
        var topLevel = vctp.Element("TopLevel")?.Elements("TypeDesc")
            .FirstOrDefault(e => (string?)e.Attribute("Index") == "1");
        if (topLevel is null || !int.TryParse((string?)topLevel.Attribute("FlatTypeID"), out var id)
            || id < 0 || id >= flat.Count)
            return new Wrapped(null, null, null, []);

        var descriptor = flat[id];
        var fields = new JsonArray();
        foreach (var child in descriptor.Elements("TypeDesc"))
        {
            // A cluster's members are references into the flat list; resolve one level so the
            // caller sees types rather than indices.
            var resolved = int.TryParse((string?)child.Attribute("TypeID"), out var cid)
                           && cid >= 0 && cid < flat.Count ? flat[cid] : child;
            fields.Add(new JsonObject
            {
                ["label"] = (string?)resolved.Attribute("Label") ?? (string?)child.Attribute("Label"),
                ["type"] = (string?)resolved.Attribute("Type"),
            });
        }

        return new Wrapped(
            (string?)descriptor.Attribute("Label"),
            (string?)descriptor.Attribute("Type"),
            Detail(descriptor),
            fields);
    }

    /// <summary>The distinguishing attributes of a refnum or tag, which `Type` alone hides.</summary>
    private static string? Detail(XElement descriptor)
    {
        var parts = new[] { "RefType", "Ident", "TypeName", "TagType" }
            .Select(a => (Name: a, Value: (string?)descriptor.Attribute(a)))
            .Where(p => p.Value is { Length: > 0 })
            .Select(p => $"{p.Name}={p.Value}")
            .ToArray();
        return parts.Length == 0 ? null : string.Join(" ", parts);
    }
}
