using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A class constant that feeds no Call terminal (problem report of 2026-11-26, finding 3): the
/// TARGET of a To More Specific Class. The composer refused it - "must feed a class terminal of a
/// Call" - and the only route was a `path` stand-in plus lvai_swap_subvis. Since 2026-11-27 it
/// 1. takes the class from the Call BEHIND the cast, also through a Case Structure's tunnels (the
///    shape of an Actor Framework message's Do.vi);
/// 2. takes it from `class=` on the constant - a qualified name or the .lvclass path - which is
///    the only way to say it when the cast's output is left unwired;
/// 3. still refuses a constant nothing names, and the refusal says how to name it.
/// Loading and compiling is measured in LabVIEW (both shapes execState 1, docs/aixml-backends.md);
/// these pin the file: the constant carries the class's links. They use the Motor / FastMotor
/// fixture (LabVIEW's own files) and SKIP where the composer or its interpreter is missing.
/// </summary>
public sealed class ComposerClassConstantTests
{
    private static string FixtureCopy()
    {
        var src = Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "classprop");
        var dir = Path.Combine(Path.GetTempPath(), "classconst-" + Guid.NewGuid().ToString("n")[..8]);
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

    private static async Task WithFixtureAsync(Func<string, Task> body)
    {
        var dir = FixtureCopy();
        try { await body(dir); }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public Task The_target_of_a_cast_takes_its_class_from_the_call_behind_the_cast() =>
        WithFixtureAsync(async dir =>
        {
            if (await ComposeInAsync(dir, """
                <VI _name="Probe.vi" description="x">
                  <Constant _name="source" type="ref{UDClassInst}" value="" uid="4200" uid_parent="root" outputs="value:4200.value" class="FastMotor.lvclass"/>
                  <Constant _name="target" type="ref{UDClassInst}" value="" uid="4201" uid_parent="root" outputs="value:4201.value"/>
                  <Node _name="To More Specific Class" uid="4210" uid_parent="root" inputs="target class:4201.value,reference:4200.value,error in:" outputs="specific class reference:4210.ref,error out:4210.err"/>
                  <Call target="Motor.lvclass\3ARead Speed.vi" uid="4220" uid_parent="root" inputs="Motor in:4210.ref,error in (no error):4210.err" outputs="Motor out:,Speed:4220.speed,error out:"/>
                  <Indicator _name="v" type="double" value="0" uid="4230" uid_parent="root" inputs="value:4220.speed"/>
                </VI>
                """) is not { } got) return;

            Assert.True(got.run.ExitCode == 0, got.run.StdErr);
            Assert.Contains("Constant 'target'", got.run.StdOut);
            Assert.Contains("(class Motor.lvclass)", got.run.StdOut);
            Assert.Contains("(class FastMotor.lvclass)", got.run.StdOut);
        });

    [Fact]
    public Task The_class_is_found_through_a_case_structures_tunnels() =>
        WithFixtureAsync(async dir =>
        {
            if (await ComposeInAsync(dir, """
                <VI _name="Probe.vi" description="x">
                  <Constant _name="source" type="ref{UDClassInst}" value="" uid="4200" uid_parent="root" outputs="value:4200.value" class="FastMotor.lvclass"/>
                  <Constant _name="target" type="ref{UDClassInst}" value="" uid="4201" uid_parent="root" outputs="value:4201.value"/>
                  <Constant _name="sel" type="bool" value="true" uid="4202" uid_parent="root" outputs="value:4202.value"/>
                  <Structure _name="Case Structure" uid="4210" uid_parent="root" selectin="4202.value">
                    <Tunnel _id="In1" uid="4211" uid_parent="4210" inputs="value:4200.value"/>
                    <Tunnel _id="In2" uid="4212" uid_parent="4210" inputs="value:4201.value"/>
                    <CaseFrame selector="True" uid="4220" uid_parent="4210" selectout="">
                      <Tunnel _id="In1" uid="4221" uid_parent="4220" outputs="value:4221.value"/>
                      <Tunnel _id="In2" uid="4222" uid_parent="4220" outputs="value:4222.value"/>
                      <Node _name="To More Specific Class" uid="4223" uid_parent="4220" inputs="target class:4222.value,reference:4221.value,error in:" outputs="specific class reference:4223.ref,error out:4223.err"/>
                      <Call target="Motor.lvclass\3ARead Speed.vi" uid="4224" uid_parent="4220" inputs="Motor in:4223.ref,error in (no error):4223.err" outputs="Motor out:,Speed:,error out:"/>
                    </CaseFrame>
                    <CaseFrame selector="False" uid="4230" uid_parent="4210" selectout="">
                      <Tunnel _id="In1" uid="4231" uid_parent="4230" outputs="value:"/>
                      <Tunnel _id="In2" uid="4232" uid_parent="4230" outputs="value:"/>
                    </CaseFrame>
                  </Structure>
                </VI>
                """) is not { } got) return;

            Assert.True(got.run.ExitCode == 0, got.run.StdErr);
            Assert.Contains("Constant 'target'", got.run.StdOut);
            Assert.Contains("(class Motor.lvclass)", got.run.StdOut);
        });

    [Fact]
    public Task A_class_named_on_the_constant_needs_nothing_downstream() =>
        WithFixtureAsync(async dir =>
        {
            // the cast's output is unwired, so nothing but the attribute names the target; once by
            // qualified name (found near the VI) and once by the .lvclass path
            var byPath = Path.Combine(dir, "Motor", "Motor.lvclass").Replace("\\", "\\5C");
            if (await ComposeInAsync(dir, $"""
                <VI _name="Probe.vi" description="x">
                  <Constant _name="source" type="ref{"{"}UDClassInst{"}"}" value="" uid="4200" uid_parent="root" outputs="value:4200.value" class="FastMotor.lvclass"/>
                  <Constant _name="target" type="ref{"{"}UDClassInst{"}"}" value="" uid="4201" uid_parent="root" outputs="value:4201.value" class="{byPath}"/>
                  <Node _name="To More Specific Class" uid="4210" uid_parent="root" inputs="target class:4201.value,reference:4200.value,error in:" outputs="specific class reference:,error out:4210.err"/>
                  <Indicator _name="error out" type="cluster{"{"}bool.status,int32.code,string.source{"}"}" value="[false,0,]" uid="4230" uid_parent="root" inputs="value:4210.err"/>
                </VI>
                """) is not { } got) return;

            Assert.True(got.run.ExitCode == 0, got.run.StdErr);
            Assert.Contains("(class Motor.lvclass)", got.run.StdOut);
            Assert.Contains("(class FastMotor.lvclass)", got.run.StdOut);
            // both constants are linked to their class file, as LabVIEW's own save links one
            Assert.Contains("Motor.lvclass", got.main);
            Assert.Contains("<FPPI", got.main);
        });

    [Fact]
    public Task A_constant_nothing_names_is_refused_and_told_how_to_name_it() =>
        WithFixtureAsync(async dir =>
        {
            if (await ComposeInAsync(dir, """
                <VI _name="Probe.vi" description="x">
                  <Constant _name="source" type="ref{UDClassInst}" value="" uid="4200" uid_parent="root" outputs="value:4200.value" class="FastMotor.lvclass"/>
                  <Constant _name="target" type="ref{UDClassInst}" value="" uid="4201" uid_parent="root" outputs="value:4201.value"/>
                  <Node _name="To More Specific Class" uid="4210" uid_parent="root" inputs="target class:4201.value,reference:4200.value,error in:" outputs="specific class reference:,error out:"/>
                </VI>
                """) is not { } got) return;

            Assert.NotEqual(0, got.run.ExitCode);
            Assert.Contains("UNSUPPORTED", got.run.StdErr);
            Assert.Contains("Constant 'target'", got.run.StdErr);
            Assert.Contains("class=", got.run.StdErr);
        });

    [Fact]
    public Task A_class_attribute_naming_no_class_is_refused_by_name() =>
        WithFixtureAsync(async dir =>
        {
            if (await ComposeInAsync(dir, """
                <VI _name="Probe.vi" description="x">
                  <Constant _name="source" type="ref{UDClassInst}" value="" uid="4200" uid_parent="root" outputs="value:4200.value" class="No Such Thing.lvclass"/>
                  <Indicator _name="v" type="path" value="" uid="4230" uid_parent="root" inputs="value:"/>
                </VI>
                """) is not { } got) return;

            Assert.NotEqual(0, got.run.ExitCode);
            Assert.Contains("No Such Thing.lvclass", got.run.StdErr);
        });
}
