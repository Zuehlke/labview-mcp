using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Issue #153, LVOOP property nodes (docs/aixml-backends.md, "Class property nodes"):
/// 1. a Property Node on a class wire composes with its oMId naming the CLASS REFNUM type. Kept at
///    the donor's 000D it named the composed VI's error cluster, and every such VI killed LabVIEW
///    on load (`Insane PropItem {size}`, 2026-11-24); a VI saved by hand in LabVIEW showed the rule.
///    Loading and running is measured in LabVIEW (write then read back); this pins the file;
/// 2. a property the class does not declare is refused by name;
/// 3. lvheap dropped a connector-pane slot's &lt;ThrallSources&gt; when it inlined a callee's
///    Function type, leaving `HasThrall="13"` with no sources - LabVIEW warned "How could we have
///    read more bytes than the size of the thrall map?" on the composed caller.
/// They use NI's SFTP File Attributes class and SKIP where it, the bundled interpreter or the
/// composer is missing.
/// </summary>
public sealed class ComposerClassPropertyNodeTests
{
    private static string Doc(string field) => $"""
        <VI _name="Probe.vi" description="x">
          <Constant _name="attrs" type="ref{"{"}UDClassInst{"}"}" value="" uid="4200" uid_parent="root" outputs="value:4200.value"/>
          <Node _name="Property Node" type="{"{"}NI_SFTP.lvlib:File Attributes.lvclass{"}"}" fields="read+{field}" uid="4201" uid_parent="root" inputs="reference:4200.value" outputs="{field}:4201.v"/>
          <Indicator _name="v" type="string" value="" uid="4202" uid_parent="root" inputs="value:4201.v"/>
        </VI>
        """;

    private static bool SftpClassHere()
    {
        const string ni = @"C:\Program Files (x86)\National Instruments";
        return Directory.Exists(ni) && Directory.EnumerateDirectories(ni, "LabVIEW 20*").Any(d =>
            File.Exists(Path.Combine(d, "vi.lib", "net", "SFTP", "File Attributes", "File Attributes.lvclass")));
    }

    private static async Task<(PyLabview.Run run, string bundle)?> ComposeKeepingAsync(string document, string dir)
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup is null || !SftpClassHere()) return null;
        var xml = Path.Combine(dir, "probe.xml");
        await File.WriteAllTextAsync(xml, document);
        var bundle = Path.Combine(dir, "bundle");
        var run = await PyLabview.RunAsync(setup.Bundle, Path.Combine(setup.ComposerDirectory, "aixml2vi.py"),
            [xml, Path.Combine(dir, "Probe.vi"), "--keep", bundle], 300, default);
        return (run, bundle);
    }

    [Fact]
    public async Task A_class_property_node_composes_with_its_oMId_naming_the_class_refnum()
    {
        var dir = Path.Combine(Path.GetTempPath(), "classprop-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            if (await ComposeKeepingAsync(Doc("Name"), dir) is not { } got) return;
            Assert.True(got.run.ExitCode == 0, got.run.StdErr);

            var setup = AixmlComposer.Locate(out _)!;
            var script = Path.Combine(dir, "omid_probe.py");
            await File.WriteAllTextAsync(script, """
                import sys, os, re
                sys.path.insert(0, sys.argv[1])
                import lvheap
                b = lvheap.Bundle(os.path.join(sys.argv[2], "Probe.xml"))
                bd = open(os.path.join(sys.argv[2], "Probe_BDHb.xml"), encoding="utf-8").read()
                o = int(re.search(r"<oMId>(\w+)</oMId>", bd).group(1), 16)
                t = b.tree(b.flat_of_consolidated(o))
                print("OMID=%s %s %s" % (t.get("Type"), t.get("RefType"), "|".join(i.get("Text") for i in t.findall("Item"))))
                main = open(os.path.join(sys.argv[2], "Probe.xml"), encoding="utf-8").read()
                print("PNOM=%d" % main.count("<PNOM"))
                print("NAME=%s" % re.search(r"<PropItemName>([^<]*)</PropItemName>", bd).group(1))
                """);
            var probe = await PyLabview.RunAsync(setup.Bundle, script, [setup.ComposerDirectory, got.bundle], 60, default);

            Assert.True(probe.ExitCode == 0, probe.StdErr);
            Assert.Contains("OMID=Refnum UDClassInst NI_SFTP.lvlib|File Attributes.lvclass", probe.StdOut);
            Assert.Contains("PNOM=1", probe.StdOut);
            Assert.Contains("NAME=\"Properties:Name\"", probe.StdOut);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_property_the_class_does_not_declare_is_refused_by_name()
    {
        var dir = Path.Combine(Path.GetTempPath(), "classprop-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            if (await ComposeKeepingAsync(Doc("Colour"), dir) is not { } got) return;

            Assert.NotEqual(0, got.run.ExitCode);
            Assert.Contains("UNSUPPORTED", got.run.StdErr);
            Assert.Contains("has no property 'Colour'", got.run.StdErr);
            Assert.Contains("Properties:Name", got.run.StdErr);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // Motor.lvclass (Speed, Name as Property Definitions at the class root, made by NI's accessor
    // wizard) and FastMotor.lvclass, its child with no properties of its own - both LabVIEW's files
    private static string FixtureCopy()
    {
        var src = Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "classprop");
        var dir = Path.Combine(Path.GetTempPath(), "classprop-" + Guid.NewGuid().ToString("n")[..8]);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(dir, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return dir;
    }

    private static async Task<(PyLabview.Run run, string main)?> ComposeInAsync(string dir, string document)
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup is null) return null;
        var xml = Path.Combine(dir, "probe.xml");
        await File.WriteAllTextAsync(xml, document);
        var bundle = Path.Combine(dir, "bundle");
        var run = await PyLabview.RunAsync(setup.Bundle, Path.Combine(setup.ComposerDirectory, "aixml2vi.py"),
            [xml, Path.Combine(dir, "Probe.vi"), "--keep", bundle], 300, default);
        var main = Path.Combine(bundle, "Probe.xml");
        return (run, File.Exists(main) ? await File.ReadAllTextAsync(main) : "");
    }

    [Fact]
    public async Task A_child_wire_reaches_a_property_its_parent_declares()
    {
        var dir = FixtureCopy();
        try
        {
            // measured 2026-11-24: written and read back through a Child wire in LabVIEW
            if (await ComposeInAsync(dir, """
                <VI _name="Probe.vi" description="x">
                  <Constant _name="m" type="ref{UDClassInst}" value="" uid="4200" uid_parent="root" outputs="value:4200.value"/>
                  <Node _name="Property Node" type="{FastMotor.lvclass}" fields="read+Speed" uid="4201" uid_parent="root" inputs="reference:4200.value" outputs="Speed:4201.v"/>
                  <Indicator _name="v" type="double" value="0" uid="4202" uid_parent="root" inputs="value:4201.v"/>
                </VI>
                """) is not { } got) return;

            Assert.True(got.run.ExitCode == 0, got.run.StdErr);
            // the row is bound to the class that DECLARES the property, through its accessor
            Assert.Matches(@"<PNOM [^>]*><LinkSaveQualName><String>Motor\.lvclass</String><String>[^<]*Read Speed\.vi</String>", got.main);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_class_constant_seeding_a_shift_register_takes_its_class_from_the_node_inside()
    {
        var dir = FixtureCopy();
        try
        {
            if (await ComposeInAsync(dir, """
                <VI _name="Probe.vi" description="x">
                  <Constant _name="m" type="ref{UDClassInst}" value="" uid="4200" uid_parent="root" outputs="value:4200.value"/>
                  <Constant _name="n" type="int32" value="3" uid="4201" uid_parent="root" outputs="value:4201.value"/>
                  <Structure _name="For Loop" count="" maxin="4201.value" maxout="" uid="4210" uid_parent="root">
                    <ShiftReg uid="4211" uid_parent="4210">
                      <Left uid="4212" uid_parent="4211" inputs="value:4200.value" outputs="value:4212.value"/>
                      <Right uid="4213" uid_parent="4211" inputs="value:4214.ref" outputs="value:4213.value"/>
                    </ShiftReg>
                    <Node _name="Property Node" type="{Motor.lvclass}" fields="read+Name" uid="4214" uid_parent="4210" inputs="reference:4212.value" outputs="reference out:4214.ref,Name:4214.v"/>
                  </Structure>
                </VI>
                """) is not { } got) return;

            Assert.True(got.run.ExitCode == 0, got.run.StdErr);
            Assert.Contains("<FPPI", got.main);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_slots_thrall_sources_survive_inlining_and_the_type_table()
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup?.Bundle is not { } bundle) return;
        var dir = Path.Combine(Path.GetTempPath(), "thrall-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // the slot shape of NI's SFTP accessors: a class `out` tied to conIdx 11
            var script = Path.Combine(dir, "thrall_probe.py");
            await File.WriteAllTextAsync(script, """
                import sys, xml.etree.ElementTree as ET
                sys.path.insert(0, sys.argv[1])
                import lvheap
                b = object.__new__(lvheap.Bundle)
                b.typedef_resolver = None
                b.flat = [ET.fromstring('<TypeDesc Type="Void" Format="inline"/>'),
                          ET.fromstring('<TypeDesc Type="NumInt32" Format="inline" Label="x"/>'),
                          ET.fromstring('<TypeDesc Type="Function" FuncFlags="0x300" Pattern="0x78" HasThrall="13" Format="inline">'
                                        '<TypeDesc TypeID="1" Flags="0x0D0B"><ThrallSources>  11</ThrallSources></TypeDesc>'
                                        '<TypeDesc TypeID="0" Flags="0x0000"/></TypeDesc>')]
                tree = b.tree(2)
                t = lvheap.TypeTable()
                flat = t.flat[t.register(tree)]
                slot = flat.find("TypeDesc")
                print("SOURCES=%r" % (slot.findtext("ThrallSources"),))
                print("ATTR=%r" % (slot.get("ThrallSources"),))
                print("TYPE=%r" % (t.flat[int(slot.get("TypeID"))].find("ThrallSources"),))
                """);
            var run = await PyLabview.RunAsync(bundle, script, [setup.ComposerDirectory], 60, default);

            Assert.True(run.ExitCode == 0, run.StdErr);
            Assert.Contains("SOURCES='  11'", run.StdOut);
            Assert.Contains("ATTR=None", run.StdOut);
            Assert.Contains("TYPE=None", run.StdOut);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
