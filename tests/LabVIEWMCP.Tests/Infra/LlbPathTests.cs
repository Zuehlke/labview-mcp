using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A path THROUGH an .llb container FILE (2026-10-15) - the C# mirror of resolve.py's llb_split,
/// which lvai_vi_terminals and the export cache use where File.Exists answers false for every
/// member of a container. Synthetic trees only: whether a member is really in the container is the
/// reader's question, not this one's.
/// </summary>
public sealed class LlbPathTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lvai-llbpath").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Container(string name = "task.llb")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "RSRC container bytes");
        return path;
    }

    [Fact]
    public void A_member_of_an_llb_FILE_splits_into_container_and_member()
    {
        var container = Container();

        var split = LlbPath.Split(Path.Combine(container, "DAQmx Clear Task.vi"));

        Assert.NotNull(split);
        Assert.Equal(container, split.Value.Container);
        Assert.Equal("DAQmx Clear Task.vi", split.Value.Member);
        Assert.True(LlbPath.Exists(Path.Combine(container, "DAQmx Clear Task.vi")));
    }

    [Fact]
    public void The_extension_is_matched_without_regard_to_case()
    {
        var container = Container("Upper.LLB");

        Assert.NotNull(LlbPath.Split(Path.Combine(container, "X.vi")));
    }

    /// <summary>323 of 344 .llbs under vi.lib are FOLDERS: their VIs are ordinary files.</summary>
    [Fact]
    public void An_llb_FOLDER_is_not_a_container()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "folder.llb")).FullName;

        Assert.Null(LlbPath.Split(Path.Combine(folder, "X.vi")));
        Assert.False(LlbPath.Exists(Path.Combine(folder, "X.vi")));
    }

    [Fact]
    public void A_path_without_an_llb_does_not_split_and_an_ordinary_file_exists()
    {
        var vi = Path.Combine(_dir, "Plain.vi");
        File.WriteAllText(vi, "x");

        Assert.Null(LlbPath.Split(vi));
        Assert.True(LlbPath.Exists(vi));
        Assert.False(LlbPath.Exists(Path.Combine(_dir, "Absent.vi")));
    }

    /// <summary>The container ITSELF is a file, not a member of one.</summary>
    [Fact]
    public void The_container_path_alone_is_not_a_member()
    {
        Assert.Null(LlbPath.Split(Container()));
    }

    /// <summary>
    /// A member has no stat of its own, so the export cache keys it on the CONTAINER's: before
    /// 2026-10-15 KeyFor answered null and NI's export of a DAQmx VI was "not cached: the entry
    /// could not be written". A changed .llb must invalidate its members' entries.
    /// </summary>
    [Fact]
    public void A_member_is_cacheable_and_keyed_on_its_containers_stat()
    {
        var install = Directory.CreateDirectory(Path.Combine(_dir, "LabVIEW 2026", "vi.lib", "DAQmx")).FullName;
        var container = Path.Combine(install, "task.llb");
        File.WriteAllText(container, "RSRC container bytes");
        var member = Path.Combine(container, "DAQmx Clear Task.vi");
        IReadOnlyList<string> roots = [Path.Combine(_dir, "LabVIEW 2026")];
        var export = Path.Combine(_dir, "export.xml");
        File.WriteAllText(export, """<VI _name="DAQmx Clear Task.vi" description="x"/>""");
        var destination = Path.Combine(_dir, "dest", "out.xml");

        var before = AixmlExportStore.KeyFor(member);
        Assert.NotNull(before);
        try
        {
            Assert.True(AixmlExportStore.Save(member, export, roots));
            Assert.True(AixmlExportStore.TryCopyTo(member, destination, roots));
            Assert.Contains("DAQmx Clear Task.vi", File.ReadAllText(destination));

            File.WriteAllText(container, "RSRC container bytes, rebuilt and longer");
            Assert.NotEqual(before, AixmlExportStore.KeyFor(member));
            Assert.False(AixmlExportStore.TryCopyTo(member, destination, roots));
        }
        finally
        {
            foreach (var file in new[] { AixmlExportStore.PathFor(before!),
                                         Path.ChangeExtension(AixmlExportStore.PathFor(before!), ".json") })
                try { File.Delete(file); } catch (IOException) { }
        }
    }
}
