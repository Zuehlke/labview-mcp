using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LabVIEWMcp.Infra;

/// <summary>
/// A VI regenerated onto the path of an existing LIBRARY MEMBER stays a member.
///
/// The composer gives a VI the owners its AIXML `_name` qualifies it with
/// (`Sample Logger.lvlib\3ASample Logger.vi`) and none when the name is bare. An agent regenerating
/// the main VI of a library from AIXML with the bare `Sample Logger.vi` got a file with NO LIBN
/// block: it was no longer a member, could not call the library's private subVIs ("This VI cannot
/// access the referenced item in private scope") and read execState 0; the same document with the
/// qualified name wrote the LIBN block and read 1 (measured 2026-11-21 on copies of
/// C:\temp\UserHints1). So before composing onto an existing file, the owners that file names for
/// itself (its LIBN block, <see cref="ViFile.Owners"/>) decide the name:
///
/// - a BARE name is qualified with them, in a copy of the document - the caller's AIXML is never
///   edited;
/// - a name already qualified with the same owners is left as it is;
/// - a name qualified with DIFFERENT owners is refused by name: moving a VI between libraries is
///   lvai_add_to_library's job, and guessing which of the two was meant would be silent;
/// - a file with no owners, or no file yet, changes nothing;
/// - a CLASS member changes nothing either: the class tools regenerate those themselves.
/// </summary>
internal static class LibraryMembership
{
    /// <summary>The AIXML spelling of the qualifier separator `:`.</summary>
    private const string Separator = "\\3A";

    /// <summary>What to do with one document. <c>Refusal</c> set means: do not compose.</summary>
    internal sealed record Decision(string? NewName, IReadOnlyList<string> Owners, string? Refusal)
    {
        internal bool Rewrites => NewName is not null;
    }

    /// <summary>The decision for an AIXML VI name against the owners an existing file names.</summary>
    internal static Decision Decide(string aixmlName, IReadOnlyList<string> existingOwners)
    {
        // LIBRARIES ONLY: a class member (an owner chain with a .lvclass in it) is regenerated
        // by the class tools, which give it its class themselves (lvai_add_class_method)
        if (existingOwners.Count == 0 ||
            existingOwners.Any(o => !o.EndsWith(".lvlib", StringComparison.OrdinalIgnoreCase)))
            return new Decision(null, [], null);
        var parts = Split(aixmlName);
        var file = parts[^1];
        var named = parts.Take(parts.Count - 1).ToList();
        if (named.Count == 0)
            return new Decision(string.Join(Separator, existingOwners.Append(file)), existingOwners, null);
        if (named.SequenceEqual(existingOwners, StringComparer.OrdinalIgnoreCase))
            return new Decision(null, existingOwners, null);
        return new Decision(null, existingOwners,
            $"The AIXML names the VI '{string.Join(":", parts)}', but the file it would overwrite " +
            $"belongs to '{string.Join(":", existingOwners)}' (its LIBN block). Write the bare name " +
            "or the file's own owners in _name; moving a VI to another library is lvai_add_to_library's job.");
    }

    /// <summary>A qualified AIXML name split at its separators - `\3A` as written, or a raw `:`.</summary>
    internal static List<string> Split(string aixmlName) =>
        [.. aixmlName.Replace(Separator, ":", StringComparison.OrdinalIgnoreCase).Split(':')];

    /// <summary>What <see cref="Prepare"/> did, for the answer, or null when nothing applied.</summary>
    internal sealed record Prepared(string PathForComposer, Decision? Decision, string? OldName)
    {
        internal JsonObject? Fact() => Decision is not { Owners.Count: > 0 } d ? null : new JsonObject
        {
            ["owners"] = new JsonArray(d.Owners.Select(o => (JsonNode?)JsonValue.Create(o)).ToArray()),
            ["kept"] = d.Refusal is null,
            ["nameRewritten"] = d.Rewrites,
            ["aixmlName"] = OldName,
            ["composedAs"] = d.NewName is { } n ? n.Replace(Separator, ":", StringComparison.Ordinal) : OldName,
            ["note"] = d.Rewrites
                ? "The VI at this path is a library member (its LIBN block), and the AIXML named it bare: " +
                  "it was composed under its qualified name in a copy of the document, so it stays a member. " +
                  "Your AIXML is unchanged."
                : "The VI at this path is a library member, and the AIXML names the same owners.",
        };
    }

    /// <summary>
    /// The document to compose onto <paramref name="viPath"/>: <paramref name="aixmlPath"/> itself,
    /// or a copy in TEMP whose root `_name` is qualified with the owners of the file it replaces.
    /// </summary>
    internal static Prepared Prepare(string aixmlPath, string viPath)
    {
        IReadOnlyList<string> owners;
        try { owners = File.Exists(viPath) ? ViFile.Owners(viPath) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                       or ArgumentException or IndexOutOfRangeException) { owners = []; }
        return Prepare(aixmlPath, owners);
    }

    /// <summary><see cref="Prepare(string, string)"/> with the owners already read.</summary>
    internal static Prepared Prepare(string aixmlPath, IReadOnlyList<string> owners)
    {
        if (owners.Count == 0) return new Prepared(aixmlPath, null, null);

        var text = File.ReadAllText(aixmlPath, Encoding.UTF8);
        var root = RootName().Match(text);
        if (!root.Success) return new Prepared(aixmlPath, null, null);
        var oldName = root.Groups["name"].Value;
        var decision = Decide(oldName, owners);
        if (!decision.Rewrites) return new Prepared(aixmlPath, decision, oldName);

        var copy = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "membership", Guid.NewGuid().ToString("N"),
                                Path.GetFileName(aixmlPath));
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        var rewritten = text[..root.Groups["name"].Index] + decision.NewName + text[(root.Groups["name"].Index + root.Groups["name"].Length)..];
        File.WriteAllText(copy, rewritten, new UTF8Encoding(false));
        return new Prepared(copy, decision, oldName);
    }

    /// <summary>The `_name` of the document's root `&lt;VI&gt;` element.</summary>
    private static Regex RootName() => new(@"<VI\b[^>]*?\s_name=""(?<name>[^""]*)""", RegexOptions.CultureInvariant);
}
