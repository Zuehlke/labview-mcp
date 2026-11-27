// =============================================================================
// ViLinks.cs - the paths a VI links to, read from its own link records (LIvi,
// LIfp, LIbd) with no LabVIEW and no pylabview, and whether an installation of
// LabVIEW holds the ones under its own folders (<vilib>, <userlib>, ...).
// =============================================================================
using System.Buffers.Binary;
using System.Text;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The path references in a LabVIEW file's link records. Each is a <c>PTH0</c> record: the four
/// letters, a big-endian u32 length of what follows, a u16 type (0 absolute or symbolic, 1
/// relative), a u16 component count, then each component as a length-prefixed Latin-1 string.
/// Measured 2026-11-14 against pylabview's decode of NI's Read Configuration (INI) File.vi (four
/// `&lt;vilib&gt;\Utility\config.llb\...` links, byte for byte) and over the 1 809 files of LabVIEW
/// 2026's examples: 20 641 records, roots `&lt;vilib&gt;` 10 135, relative 8 530,
/// `&lt;extravilib&gt;` 1 400, `&lt;helpdir&gt;` 161, `&lt;resource&gt;` 63, `&lt;userlib&gt;` 48.
/// Only the TOP-LEVEL link blocks are read: a malleable VI's instance carries its own resource
/// map inside the caller, and its links are its own.
/// </summary>
internal static class ViLinks
{
    /// <summary>One linked path; <see cref="Root"/> is its symbolic first component, if any.</summary>
    internal sealed record PathRef(int Type, IReadOnlyList<string> Parts)
    {
        public string? Root => Parts.Count > 0 && Parts[0].StartsWith('<') && Parts[0].EndsWith('>') ? Parts[0] : null;
        public override string ToString() => string.Join('\\', Parts);
    }

    /// <summary>The roots that name a folder of the LabVIEW installation, and that folder.</summary>
    internal static readonly IReadOnlyDictionary<string, string> InstallationRoots =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["<vilib>"] = "vi.lib",
            ["<userlib>"] = "user.lib",
            ["<instrlib>"] = "instr.lib",
            ["<resource>"] = "resource",
            ["<menus>"] = "menus",
        };

    /// <summary>Every path reference in the file's top-level link blocks. Throws on a file that is not a resource file.</summary>
    internal static IReadOnlyList<PathRef> Read(byte[] file)
    {
        var refs = new List<PathRef>();
        foreach (var type in new[] { "LIvi", "LIfp", "LIbd" })
            foreach (var block in RsrcMap.Blocks(file, type))
                refs.AddRange(Scan(block));
        return refs;
    }

    /// <summary>The <c>PTH0</c> records in one block; a record whose length does not add up is skipped.</summary>
    internal static IEnumerable<PathRef> Scan(byte[] block)
    {
        var marker = "PTH0"u8.ToArray();
        for (var at = block.AsSpan().IndexOf(marker); at >= 0;)
        {
            if (Parse(block, at) is { } path) yield return path;
            var next = block.AsSpan(at + 4).IndexOf(marker);
            at = next < 0 ? -1 : at + 4 + next;
        }
    }

    private static PathRef? Parse(byte[] block, int at)
    {
        if (at + 12 > block.Length) return null;
        var length = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(at + 4));
        if (length < 4 || at + 8 + length > block.Length) return null;
        var type = BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(at + 8));
        var count = BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(at + 10));
        var parts = new List<string>(count);
        var p = at + 12;
        for (var i = 0; i < count; i++)
        {
            if (p >= block.Length) return null;
            int n = block[p];
            if (p + 1 + n > block.Length) return null;
            parts.Add(Encoding.Latin1.GetString(block, p + 1, n));
            p += 1 + n;
        }
        return p - (at + 8) == length ? new PathRef(type, parts) : null;
    }

    /// <summary>What an installation says about one linked path under its own folders.</summary>
    internal enum Presence { Present, Missing, MissingFromContainer }

    /// <summary>
    /// Whether <paramref name="installRoot"/> holds a path under one of its own folders. A component
    /// that is an existing FILE is a container (an `.llb`); the rest must name a member in it, which
    /// is checked as the member's length-prefixed name in the container's bytes - a heuristic, and
    /// named one: a name in the container's directory, not a decoded member list.
    /// </summary>
    internal static Presence Check(string installRoot, PathRef link)
    {
        var folder = InstallationRoots[link.Root!];
        var path = Path.Combine(installRoot, folder);
        for (var i = 1; i < link.Parts.Count; i++)
        {
            path = Path.Combine(path, link.Parts[i]);
            if (File.Exists(path) && i < link.Parts.Count - 1)
                // a class's private data control is linked THROUGH its .lvclass
                // (`Actor.lvclass\Actor.ctl`, 15 of NI's AF examples) and lives inside that XML
                // file - the owner being there is the answer; an .llb or packed library holds
                // real members, looked up by name
                return EmbedsMembers(path) || ContainerHolds(path, link.Parts[^1])
                    ? Presence.Present : Presence.MissingFromContainer;
        }
        return File.Exists(path) || Directory.Exists(path) ? Presence.Present : Presence.Missing;
    }

    private static bool EmbedsMembers(string ownerFile) =>
        Path.GetExtension(ownerFile).ToLowerInvariant() is ".lvclass" or ".lvlib" or ".xctl" or ".lvsc";

    private static readonly Dictionary<string, byte[]> Containers = new(StringComparer.OrdinalIgnoreCase);

    private static bool ContainerHolds(string container, string member)
    {
        byte[] bytes;
        lock (Containers)
        {
            if (!Containers.TryGetValue(container, out bytes!))
            {
                try { bytes = File.ReadAllBytes(container); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { bytes = []; }
                if (Containers.Count > 200) Containers.Clear();
                Containers[container] = bytes;
            }
        }
        var name = Encoding.Latin1.GetBytes(member);
        if (name.Length is 0 or > 255) return false;
        var needle = new byte[name.Length + 1];
        needle[0] = (byte)name.Length;
        name.CopyTo(needle, 1);
        return bytes.AsSpan().IndexOf(needle) >= 0;
    }
}
