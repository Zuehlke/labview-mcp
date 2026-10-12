using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// <c>lvai_add_class_method</c>'s retype, sequenced and reported per terminal - and the vi-only
/// re-run that left a class without its member.
///
/// THE DEFECT. Measured 2026-10-12 (C:\Temp\BackendCompare, pylabview backend): a method answered
/// <c>terminals retyped: 2</c> with every stage at error 0, and the saved file still had one class
/// terminal as a path - only the on-disk verify saw it. The helper's diagram had NO data dependency
/// between its Replace loop and the membership-and-save chain: <c>LVClass.Open</c> took its error
/// in from the <c>Controls[]</c> read BEFORE the loop, so LabVIEW was free to run
/// AddItemFromMemory, SetWireRule and both saves in parallel with the Replaces. And the Replace's
/// own error out was unwired, so "retyped" counted names FOUND. Measured on the same orphaned VI:
/// with the old diagram the panel reference was dead after the loop 2 of 2 runs (and one Replace
/// answered 1055, invalid reference, and still counted); with the class chain sequenced after the
/// loop, the read-back was complete 2 of 2.
/// </summary>
public class ClassMethodRetypeSequenceTests
{
    private static readonly string Helper =
        RepoTree.Path("scripts", ClassMethodTools.HelperFileName);

    private static ClassMethodTools.MethodRequest Method(params string[] terminals) =>
        new(@"C:\x\Scaled Value.vi", null,
            [.. terminals.Select(t => new ClassMethodTools.ClassTerminal(t, null))], null, null);

    private static JsonObject ArrayValue(string name, string element, params string[] values) => new()
    {
        ["type"] = "Array",
        ["xml"] = $"<Array>\r\n  <Name>{name}</Name>\r\n  <Dimsize>{values.Length}</Dimsize>\r\n" +
                  (values.Length == 0
                      // LabVIEW writes one TEMPLATE element into an empty array
                      ? $"  <{element}>\r\n    <Name></Name>\r\n    <Val></Val>\r\n  </{element}>\r\n"
                      : string.Concat(values.Select(v =>
                          $"  <{element}>\r\n    <Name></Name>\r\n    <Val>{v}</Val>\r\n  </{element}>\r\n"))) +
                  "</Array>",
    };

    private static JsonObject Values(string[] codes, string[] classes, string[] names) => new()
    {
        ["replace error codes"] = ArrayValue("replace error codes", "I32", codes),
        ["classes after replace"] = ArrayValue("classes after replace", "String", classes),
        ["names after replace"] = ArrayValue("names after replace", "String", names),
    };

    // ------------------------------------------------------------------ the helper's dataflow

    private static XElement ByTarget(string target) =>
        XElement.Load(Helper).Descendants("Node").Single(n => (string?)n.Attribute("target") == target);

    private static string ReplaceLoopUid() =>
        (string)ByTarget("Replace").Ancestors("Structure")
            .Last(s => (string?)s.Attribute("_name") == "For Loop").Attribute("uid")!;

    [Fact]
    public void The_class_open_waits_for_the_replace_loop()
    {
        var tree = XElement.Load(Helper);
        var open = tree.Descendants("Node").Single(n => (string?)n.Attribute("target") == "LVClass.Open");
        Assert.True(DependsOnStructureIn(tree, open, ReplaceLoopUid()),
            "LVClass.Open (and so AddItemFromMemory, SetWireRule and both saves) must be " +
            "downstream of the Replace loop - in parallel, the membership step invalidated the " +
            "panel references mid-loop and a save could precede the last Replace.");
    }

    [Fact]
    public void Every_save_is_downstream_of_the_replace_loop()
    {
        var tree = XElement.Load(Helper);
        foreach (var target in new[] { "AddItemFromMemory", "SetWireRule", "Save.Instrument", "Save" })
        {
            var node = tree.Descendants("Node").Single(n => (string?)n.Attribute("target") == target);
            Assert.True(DependsOnStructureIn(tree, node, ReplaceLoopUid()), target);
        }
    }

    [Fact]
    public void The_replace_error_out_is_wired_and_reported()
    {
        var replace = ByTarget("Replace");
        Assert.DoesNotContain("error out:,", (string?)replace.Attribute("outputs") ?? "");
        var names = XElement.Load(Helper).Descendants("Indicator")
            .Select(i => (string?)i.Attribute("_name")).ToList();
        Assert.Contains("replace error codes", names);
        Assert.Contains("classes after replace", names);
        Assert.Contains("names after replace", names);
    }

    [Fact]
    public void Each_replace_reads_its_control_reference_afresh()
    {
        // The Controls[] the Replace indexes into is read INSIDE the Replace loop, not once before
        // it: a Replace (or anything else that relinks the VI) can invalidate the references a
        // single up-front read handed out - Error 1055 was measured on the second terminal.
        var tree = XElement.Load(Helper);
        var loop = ReplaceLoopUid();
        var reads = tree.Descendants("Node")
            .Where(n => (string?)n.Attribute("fields") == "read+Controls[]")
            .Where(n => n.Ancestors("Structure").Any(s => (string?)s.Attribute("uid") == loop));
        Assert.Single(reads);
    }

    private static bool DependsOnStructureIn(XElement tree, XElement target, string uid)
    {
        static string[] Nets(string? spec) => spec is null ? [] :
            [.. spec.Split(',').Select(p => p[(p.LastIndexOf(':') + 1)..]).Where(n => n.Length > 0)];
        var producers = new Dictionary<string, XElement>();
        foreach (var e in tree.Descendants())
            foreach (var net in Nets((string?)e.Attribute("outputs"))) producers.TryAdd(net, e);

        var seen = new HashSet<XElement>();
        var todo = new Stack<XElement>(Nets((string?)target.Attribute("inputs"))
            .Where(producers.ContainsKey).Select(n => producers[n]));
        while (todo.Count > 0)
        {
            var e = todo.Pop();
            if (!seen.Add(e)) continue;
            if (e.AncestorsAndSelf().Any(a => a.Name == "Structure" && (string?)a.Attribute("uid") == uid))
                return true;
            foreach (var net in Nets((string?)e.Attribute("inputs")))
                if (producers.TryGetValue(net, out var p)) todo.Push(p);
        }
        return false;
    }

    // ------------------------------------------------------------------ what the tool reads

    [Fact]
    public void A_refused_replace_is_named_and_not_counted_as_retyped()
    {
        var r = ClassMethodTools.RetypeReport(Method("Probe in", "Probe out"),
            Values(["0", "1055"], [], []));
        Assert.Contains("'Probe out' (Error 1055)", r.Hint);
        Assert.Equal(1055, r.Report["terminals"]![1]!["replaceErrorCode"]!.GetValue<int>());
    }

    [Fact]
    public void A_replace_that_answered_0_and_left_a_path_is_an_in_memory_no_op()
    {
        var r = ClassMethodTools.RetypeReport(Method("Probe in", "Probe out"),
            Values(["0", "0"], ["LabVIEWClassControl", "Cluster", "Path"],
                   ["Probe in", "error in", "Probe out"]));
        Assert.Equal(["Probe out"], r.StillPathInMemory);
        Assert.Contains("in memory", r.Hint);
        Assert.True(r.Report["readBack"]!.GetValue<bool>());
    }

    [Fact]
    public void A_clean_retype_has_no_hint_and_reads_both_back_as_classes()
    {
        var r = ClassMethodTools.RetypeReport(Method("Probe in", "Probe out"),
            Values(["0", "0"],
                   ["LabVIEWClassControl", "Cluster", "LabVIEWClassControl", "Digital", "Cluster"],
                   ["Probe in", "error in", "Probe out", "Scaled Value", "error out"]));
        Assert.Null(r.Hint);
        Assert.Empty(r.StillPathInMemory);
        Assert.Equal("LabVIEWClassControl",
            r.Report["terminals"]![1]!["classAfterReplace"]!.GetValue<string>());
    }

    [Fact]
    public void A_name_not_on_the_panel_is_minus_one_and_says_so()
    {
        var r = ClassMethodTools.RetypeReport(Method("Probe in", "Prbe out"),
            Values(["0", "-1"], [], []));
        Assert.Contains("Not on the panel: 'Prbe out'", r.Hint);
    }

    [Fact]
    public void An_empty_array_is_empty_not_one_blank_element()
    {
        // LabVIEW writes a template element into an EMPTY array; it read as one blank name, so an
        // empty read-back looked like a read-back that ran.
        var values = new JsonObject { ["x"] = ArrayValue("x", "String") };
        Assert.Empty(ClassMethodTools.Names(values, "x"));
        var r = ClassMethodTools.RetypeReport(Method("a"), Values([], [], []));
        Assert.False(r.Report["readBack"]!.GetValue<bool>());
    }

    // ------------------------------------------------------------------ the vi-only re-run

    [Fact]
    public void The_owning_class_is_read_off_the_LIBN_block()
    {
        const string main = """
            <RSRC>
              <LIBN>
                <!-- Library Names -->
                <Section Index="0" Format="inline">
                  <Library>Probe.lvclass</Library>
                  </Section>
                </LIBN>
            </RSRC>
            """;
        var libs = ClassMethodTools.OwningLibrariesFromMainXml(main);
        Assert.Equal(["Probe.lvclass"], libs);
        Assert.True(ClassMethodTools.NamesClassAsOwner(libs, @"C:\x\Probe\Probe.lvclass"));
        Assert.False(ClassMethodTools.NamesClassAsOwner(libs, @"C:\x\Other\Other.lvclass"));
    }

    [Fact]
    public void A_freshly_composed_VI_names_no_owner()
    {
        Assert.Empty(ClassMethodTools.OwningLibrariesFromMainXml("<RSRC><LVSR/></RSRC>"));
        Assert.False(ClassMethodTools.NamesClassAsOwner([], @"C:\x\Probe.lvclass"));
        Assert.False(ClassMethodTools.NamesClassAsOwner(null, @"C:\x\Probe.lvclass"));
    }

    [Fact]
    public void A_verify_with_every_terminal_typed_but_no_class_entry_says_it_is_not_a_member()
    {
        var detail = ClassMethodTools.VerifyFailureDetail(new JsonObject
        {
            ["ran"] = true,
            ["classTypedTerminals"] = 2,
            ["pathStandInsLeft"] = 0,
            ["expectedPathStandInsLeft"] = null,
            ["listedInClassFile"] = false,
        }, 2);
        Assert.False(detail["listedInClassFile"]!.GetValue<bool>());
        Assert.Contains("does NOT list", detail["hint"]!.GetValue<string>());
    }
}
