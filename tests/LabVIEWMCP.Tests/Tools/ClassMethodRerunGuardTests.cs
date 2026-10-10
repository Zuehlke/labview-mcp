// The parts of lvai_add_class_method's loaded-callers guard that need no LabVIEW: the names it asks
// for, and how it reads the answer.
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public sealed class ClassMethodRerunGuardTests
{
    [Fact]
    public void Members_are_asked_for_by_their_library_qualified_name()
    {
        var dir = Directory.CreateTempSubdirectory("rerun-guard").FullName;
        try
        {
            var cls = Path.Combine(dir, "Counter.lvclass");
            File.WriteAllText(cls,
                "<?xml version='1.0' encoding='UTF-8'?>\n<LVClass LVVersion=\"26008000\">\n" +
                "\t<Property Name=\"NI.Lib.ContainingLib\" Type=\"Str\">Lab.lvlib</Property>\n</LVClass>\n");

            Assert.Equal(["Lab.lvlib:Counter.lvclass:Start.vi", "Lab.lvlib:Counter.lvclass:log.vi"],
                ClassMethodTools.QualifiedMemberNames(cls, ["Start.vi", @"C:\x\log.vi"]));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Callers_are_grouped_per_member_without_duplicates()
    {
        var grouped = ClassMethodTools.GroupCallers(
        [
            "C.lvclass:Start.vi|C.lvclass:Actor Core.vi",
            "C.lvclass:Start.vi|C.lvclass:Actor Core.vi",
            "C.lvclass:Start.vi|Launcher.vi",
            "C.lvclass:log.vi|C.lvclass:Stop Core.vi",
            "garbage",
            "|nobody",
        ]);

        Assert.Equal(["C.lvclass:Start.vi", "C.lvclass:log.vi"], grouped.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(["C.lvclass:Actor Core.vi", "Launcher.vi"], grouped["C.lvclass:Start.vi"]);
    }

    [Fact]
    public void No_pairs_means_no_callers() => Assert.Empty(ClassMethodTools.GroupCallers([]));
}
