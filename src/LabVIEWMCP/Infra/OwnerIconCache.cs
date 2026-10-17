// ============================================================================================
// OwnerIconCache.cs - a class's or library's icon, read back through LabVIEW ONCE and kept as a
// PNG, keyed on the icon data the owner file itself carries.
// ============================================================================================
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The owner's icon is ONE property of its file, <c>NI.Lib.Icon</c> (Type Bin). Reading it as a
/// PNG takes LabVIEW - a carrier VI, LVClass.Open / Library.Open, Get Icon, Save VI Icon to File -
/// and the icon tools needed it on every call: <c>lvai_set_vi_icon keepOwnLayer</c> once per VI,
/// <c>lvai_read_icons</c> once per owner. The user's wish of 2026-10-17: read it once and keep it.
///
/// THE KEY IS THE CONTENT. A SHA-256 over the property's text as the file holds it, so whether a
/// cached PNG still describes the owner is answered by reading one XML file - no LabVIEW - and an
/// icon changed by anyone, in the IDE or by lvai_set_owner_icon, simply has another key. An owner
/// with no <c>NI.Lib.Icon</c> line (LabVIEW's default icon) is not cached: there is nothing in the
/// file to key it on. Two owners with identical icons share one entry, which is right, because
/// the read-back depends on the icon data alone.
/// </summary>
internal static class OwnerIconCache
{
    internal const string IconProperty = "NI.Lib.Icon";

    /// <summary>Where the PNGs live, beside the AIXML export cache.</summary>
    public static string Folder => Path.Combine(CacheDirectory.Root, "owner-icons");

    /// <summary>The cache key of an owner file, or null when it carries no icon property.</summary>
    public static string? Key(string ownerPath)
    {
        try
        {
            var root = XDocument.Load(ownerPath).Root;
            var icon = root?.Elements("Property")
                .FirstOrDefault(p => (string?)p.Attribute("Name") == IconProperty)?.Value;
            if (string.IsNullOrEmpty(icon)) return null;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(icon)))[..32].ToLowerInvariant();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Copy the cached PNG for the owner's CURRENT icon to <paramref name="destination"/>. True on a
    /// hit. The bytes are written, not copied, so the destination carries a fresh write time - the
    /// tools judge a read-back by "written during this call".
    /// </summary>
    public static bool TryRestore(string ownerPath, string destination, out string? key)
    {
        key = Key(ownerPath);
        if (key is null) return false;
        var cached = Path.Combine(Folder, key + ".png");
        if (!File.Exists(cached)) return false;
        try
        {
            var bytes = File.ReadAllBytes(cached);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            File.WriteAllBytes(destination, bytes);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Keep <paramref name="png"/> - a read-back LabVIEW just wrote - as the icon of the owner's
    /// CURRENT file content. The key is taken now, after the read, so a read that followed a
    /// Set Icon + Save is filed under the icon it actually read. Returns the key, or null when the
    /// owner carries no icon property or the PNG could not be kept.
    /// </summary>
    public static string? Store(string ownerPath, string png)
    {
        var key = Key(ownerPath);
        if (key is null || !File.Exists(png)) return null;
        try
        {
            Directory.CreateDirectory(Folder);
            var target = Path.Combine(Folder, key + ".png");
            var temp = target + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(temp, File.ReadAllBytes(png));
            File.Move(temp, target, overwrite: true);
            return key;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
