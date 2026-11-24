using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Issue #153, LVOOP property nodes (docs/aixml-backends.md, "Class property nodes"):
/// 1. every VI the composer built with a Property Node on a class wire KILLED LabVIEW on load
///    (`Insane PropItem {size}`, 2026-11-24), so it is refused by name until that is understood -
///    before, a reader export with one composed into a VI that took LabVIEW down;
/// 2. lvheap dropped a connector-pane slot's &lt;ThrallSources&gt; when it inlined a callee's
///    Function type, leaving `HasThrall="13"` with no sources - LabVIEW warned "How could we have
///    read more bytes than the size of the thrall map?" on the composed caller.
/// SKIPS where the bundled interpreter or the composer is missing.
/// </summary>
public sealed class ComposerClassPropertyNodeTests
{
    [Fact]
    public async Task A_property_node_on_a_class_wire_is_refused_by_name()
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup is null || PyLabview.Locate() is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "classprop-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var xml = Path.Combine(dir, "probe.xml");
            await File.WriteAllTextAsync(xml, """
                <VI _name="Probe.vi" description="x">
                  <Constant _name="attrs" type="ref{UDClassInst}" value="" uid="4200" uid_parent="root" outputs="value:4200.value"/>
                  <Node _name="Property Node" type="{NI_SFTP.lvlib:File Attributes.lvclass}" fields="read+Name" uid="4201" uid_parent="root" inputs="reference:4200.value" outputs="Name:4201.Name"/>
                  <Indicator _name="Name" type="string" value="" uid="4202" uid_parent="root" inputs="value:4201.Name"/>
                </VI>
                """);
            var result = await AixmlComposer.ComposeAsync(setup, xml, Path.Combine(dir, "Probe.vi"), [], 300, default);

            Assert.False(result.Written);
            Assert.True(result.Refused, result.Message);
            Assert.Contains("LVOOP property", result.Message);
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
