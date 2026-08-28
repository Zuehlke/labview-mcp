using System.Text.RegularExpressions;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The class-creation helper's AIXML, guarded against one specific regression.
///
/// WHY A REFERENCE LEAK IS A CORRECTNESS BUG HERE, not a resource one. NI's
/// <c>Add Class to Project (path).vi</c> hands back a <c>Class</c> refnum. While that refnum is
/// open, LabVIEW keeps the new class IN MEMORY - and it survives the project being closed. The next
/// <c>lvai_create_class</c> into the same project then opens a .lvproj that lists that class,
/// LabVIEW will not bind the item to the copy already in memory, so
/// <c>CLSUIP_GetAllClassesInProject.vi</c> does not report it, the parent search answers -1, and the
/// child class is created WITH NO PARENT and no error at all.
///
/// Measured 2026-08-28 on a controlled pair. Leaked: `parent index` -1 for a parent the .lvproj
/// plainly listed, and only a LabVIEW RESTART cleared it - three restarts were needed for one
/// two-class run, and the same leak is why a deleted .lvclass answers Error 1614 when recreated.
/// With the single <c>Close Reference</c> added: `parent index` 0, and a full two-class,
/// twelve-accessor run with no restart anywhere.
///
/// The symptom points nowhere near the cause, which is why this is pinned by a test rather than
/// left to a comment: it looks exactly like a stale project file, and was diagnosed as one twice.
/// </summary>
public class ClassToolsHelperAixmlTests
{
    private static string Aixml()
    {
        var path = Res.FindRepoFile("scripts/lvai_create_class.xml");
        Assert.NotNull(path);
        return File.ReadAllText(path!);
    }

    [Fact]
    public void Ships_its_helper_source_calling_NIs_own_providers()
    {
        var xml = Aixml();
        Assert.Contains("Add Class to Project (path).vi", xml);
        Assert.Contains("Add Member Data to Private Data Control.vi", xml);
        Assert.Contains("CLSUIP_GetAllClassesInProject.vi", xml);
    }

    [Fact]
    public void Closes_the_class_reference_the_provider_returns()
    {
        var xml = Aixml();

        // The refnum is produced as `Class:<uid>.Class` by the Add Class call, and consumed by a
        // Close Reference node wiring `reference:<uid>.Class`. Both halves are asserted so that
        // renaming the uid on one side alone cannot pass.
        var produced = Regex.Match(xml, @"outputs=""[^""]*\bClass:(\d+)\.Class\b");
        Assert.True(produced.Success,
            "The Add Class call no longer names its Class output - if the provider's terminal was "
            + "renamed, follow it and keep the Close Reference below wired to the new name.");

        var uid = produced.Groups[1].Value;
        Assert.Matches(
            new Regex($@"_name=""Close Reference""[^/]*?inputs=""reference:{uid}\.Class\b"),
            xml);
    }

    [Fact]
    public void Closes_every_reference_it_opens()
    {
        var xml = Aixml();

        // The other three were closed from the start; they are pinned here so that a future edit
        // adding a fourth reference meets a test that already counts them.
        var closes = Regex.Matches(xml, @"_name=""Close Reference""[^/]*?inputs=""reference:([^,""]+)")
                          .Select(m => m.Groups[1].Value)
                          .ToList();

        Assert.Equal(4, closes.Count);
        Assert.Contains(closes, r => r.EndsWith(".Class"));           // the new class
        Assert.Contains(closes, r => r.EndsWith(".vi reference"));    // the carrier VI
        Assert.Contains(closes, r => r.EndsWith(".app"));             // the application
        Assert.Contains(closes, r => r.EndsWith(".proj"));            // the project
    }
}
