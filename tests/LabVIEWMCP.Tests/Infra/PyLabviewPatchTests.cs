using System.Text.Json;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Guards the patches applied to the vendored pylabview at provisioning time.
///
/// The failure these tests exist for is silence. A patch is a Find/Replace against a line of
/// upstream source; when upstream edits that line the Find stops matching, the bundle assembles
/// perfectly, and the bug the patch existed to fix is quietly back. provision.ps1 refuses that at
/// assembly time - but assembly happens on a developer's machine, not in CI, so these tests are
/// what notice after a re-vendor.
///
/// They read the SAME patches.json provision.ps1 reads, against the SAME vendored tree, so a green
/// suite means the next provisioning run will apply cleanly.
/// </summary>
public sealed class PyLabviewPatchTests
{
    private static string? RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "tools", "pylabview", "vendor")))
                return dir.FullName;
        }
        return null;
    }

    private sealed record Patch(string Id, string File, string Find, string Replace, string Why);

    private static List<Patch>? LoadPatches(out string vendorRoot)
    {
        vendorRoot = "";
        var root = RepositoryRoot();
        if (root is null) return null;            // binary-only layout: nothing to check
        vendorRoot = Path.Combine(root, "tools", "pylabview", "vendor");
        var file = Path.Combine(root, "tools", "pylabview", "patches", "patches.json");
        if (!System.IO.File.Exists(file)) return [];

        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
        var list = new List<Patch>();
        foreach (var e in doc.RootElement.GetProperty("patches").EnumerateArray())
        {
            list.Add(new Patch(
                e.GetProperty("id").GetString()!,
                e.GetProperty("file").GetString()!,
                e.GetProperty("find").GetString()!,
                e.GetProperty("replace").GetString()!,
                e.GetProperty("why").GetString()!));
        }
        return list;
    }

    [Fact]
    public void EveryPatchTargetsAFileThatExists()
    {
        var patches = LoadPatches(out var vendor);
        if (patches is null) return;

        foreach (var p in patches)
        {
            Assert.True(System.IO.File.Exists(Path.Combine(vendor, p.File)),
                $"patch '{p.Id}' targets '{p.File}', which is not in the vendored tree.");
        }
    }

    /// <summary>
    /// The important one. Exactly one occurrence: zero means upstream moved and the patch is dead,
    /// more than one means the replacement would hit code it was never measured against.
    /// </summary>
    [Fact]
    public void EveryPatchFindStringOccursExactlyOnce()
    {
        var patches = LoadPatches(out var vendor);
        if (patches is null) return;

        foreach (var p in patches)
        {
            var path = Path.Combine(vendor, p.File);
            if (!System.IO.File.Exists(path)) continue;    // covered by the test above
            var text = System.IO.File.ReadAllText(path);

            var count = 0;
            for (var i = text.IndexOf(p.Find, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(p.Find, i + 1, StringComparison.Ordinal)) count++;

            Assert.True(count == 1,
                $"patch '{p.Id}': its Find string occurs {count} time(s) in {p.File}, expected 1. " +
                "Upstream has changed that line - re-derive the patch, or drop it if the fix has " +
                "landed upstream. A patch that matches nothing looks exactly like one that worked.");
        }
    }

    /// <summary>
    /// vendor\ must stay pristine. If a replacement is already there, someone edited the vendored
    /// tree instead of the bundle, and VENDOR.md's "local changes: none" has quietly become false.
    /// </summary>
    [Fact]
    public void NoPatchIsAlreadyAppliedToTheVendoredTree()
    {
        var patches = LoadPatches(out var vendor);
        if (patches is null) return;

        foreach (var p in patches)
        {
            var path = Path.Combine(vendor, p.File);
            if (!System.IO.File.Exists(path)) continue;
            Assert.DoesNotContain(p.Replace, System.IO.File.ReadAllText(path), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A patch without a stated reason is a patch nobody can evaluate later. This is the one piece
    /// of documentation the format can actually enforce.
    /// </summary>
    [Fact]
    public void EveryPatchExplainsItself()
    {
        var patches = LoadPatches(out _);
        if (patches is null) return;

        foreach (var p in patches)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Id));
            Assert.True(p.Why.Length > 80,
                $"patch '{p.Id}' has a {p.Why.Length}-character reason. Say what breaks without it " +
                "and how that was measured, or the next reader has to re-derive it.");
            Assert.NotEqual(p.Find, p.Replace);
        }
    }
}
