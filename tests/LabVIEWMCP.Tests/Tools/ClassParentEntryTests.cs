using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_create_class lists a parent in the user's project for the reader - but not one from the
/// LabVIEW installation: a grpc build of 2026-10-17 got Actor.lvclass at target level with a URL
/// into Program Files.
/// </summary>
public class ClassParentEntryTests
{
    [Fact]
    public void A_class_under_a_folder_holding_LabVIEW_exe_is_installation_code()
    {
        var root = Path.Combine(Path.GetTempPath(), "LabVIEWMCP-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var lv = Path.Combine(root, "LabVIEW 2026");
            var actor = Path.Combine(lv, "vi.lib", "ActorFramework", "Actor", "Actor.lvclass");
            var own = Path.Combine(root, "Project", "Base", "Base.lvclass");
            Directory.CreateDirectory(Path.GetDirectoryName(actor)!);
            Directory.CreateDirectory(Path.GetDirectoryName(own)!);
            File.WriteAllText(Path.Combine(lv, "LabVIEW.exe"), "");
            File.WriteAllText(actor, "");
            File.WriteAllText(own, "");

            Assert.True(ClassTools.InLabViewInstallation(actor));
            Assert.False(ClassTools.InLabViewInstallation(own));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
