using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Grpc;

namespace LabVIEWMcp.Tools;

/// <summary>
/// The `icon` step every VI-producing tool ends with: a drawn 32x32 icon derived from the VI's own
/// name, unless the caller passes its own lines or switches it off.
///
/// WHY IT IS IN THE TOOLS AND NOT IN THE AGENTS. Every agent definition had a phase "Icons - do not
/// skip this", and the icons were skipped anyway: on 2026-10-17 a class build shipped fifteen VIs and
/// a Caraya suite with LabVIEW's blank default icon, and its report listed it under "Not done". A step
/// that does not change whether the VI WORKS is the first one an agent drops, however loudly the
/// definition asks. So the tool that writes the VI draws the icon itself; an agent can still pass a
/// better text, it can no longer forget.
///
/// TWO SAFETY RULES. It runs LAST in each tool - a later ConvertAIXMLToVI over the path destroys
/// it. And lvai_set_vi_icon FLATTENS the icon, so a VI whose owning class or library carries an icon
/// (`NI.Lib.Icon` in the owner file) is left alone: its member icon is the owner's template, and
/// replacing it would destroy that (docs/class-icons.md). The save leaves compiled code behind, so a
/// VI that was source-only before is stripped again (the no-compiled-code rule).
/// </summary>
internal static class DefaultIcon
{
    /// <summary>Parameter text shared by every tool that carries the step.</summary>
    internal const string ParameterDescription = """
        The VI's ICON, drawn as the last step: "L1|L2|L3" - banner text and up to two lines, 5
        characters each, drawable A-Z 0-9 space - . / : + #. Omit it to have one DERIVED from the
        VI's name (and its class or the tested subject); pass "none" to leave LabVIEW's blank icon.
        A VI whose class or library has its own icon keeps that template and is not touched.
        """;

    /// <summary>The kind of VI, which decides the banner of a derived icon.</summary>
    internal enum Kind { Plain, ClassMember, Test, Runner }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        { "a", "an", "the", "of", "to", "for", "and", "vi", "name", "lvclass", "lvlib" };

    /// <summary>
    /// The three lines of a derived icon. Plain: banner from the first word of the VI name, lines
    /// from the next two. Class member: banner from the class, lines from the method name. Test and
    /// runner: banner TEST / RUN, lines from the subject (or the VI name without "Test"/"Run").
    /// </summary>
    internal static (string L1, string? L2, string? L3) Derive(string viPath, Kind kind, string? owner = null)
    {
        var words = Words(Path.GetFileNameWithoutExtension(viPath));
        var ownerWords = owner is null ? [] : Words(Path.GetFileNameWithoutExtension(owner));
        switch (kind)
        {
            case Kind.ClassMember when ownerWords.Count > 0:
                return (Abbreviate(ownerWords[0]), At(words, 0), At(words, 1));
            case Kind.Test or Kind.Runner:
            {
                var banner = kind == Kind.Test ? "TEST" : "RUN";
                var subject = ownerWords.Count > 0
                    ? ownerWords
                    : words.Where(w => !w.Equals("test", StringComparison.OrdinalIgnoreCase)
                                    && !w.Equals("tests", StringComparison.OrdinalIgnoreCase)
                                    && !w.Equals("run", StringComparison.OrdinalIgnoreCase)).ToList();
                return (banner, At(subject, 0), At(subject, 1));
            }
            default:
                return words.Count == 0 ? ("VI", null, null) : (Abbreviate(words[0]), At(words, 1), At(words, 2));
        }
    }

    /// <summary>"L1|L2|L3" parsed; null for "none", or when nothing usable was given.</summary>
    internal static (string L1, string? L2, string? L3)? Parse(string text)
    {
        if (text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = text.Split('|').Select(p => p.Trim()).ToArray();
        if (parts.Length == 0 || parts[0].Length == 0) return null;
        return (parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null,
                parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null);
    }

    /// <summary>
    /// The owner file of a VI that carries its OWN icon - the class or library within three folders
    /// up that lists the VI and has an <c>NI.Lib.Icon</c> line - or null. Such a member's icon is the
    /// owner's template layer, which a flattening set would destroy.
    /// </summary>
    internal static string? OwnerWithIcon(string viPath)
    {
        var name = Path.GetFileName(viPath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(viPath));
        for (var up = 0; up < 4 && dir is not null; up++, dir = Path.GetDirectoryName(dir))
        {
            IEnumerable<string> owners;
            try { owners = Directory.EnumerateFiles(dir, "*.lvclass").Concat(Directory.EnumerateFiles(dir, "*.lvlib")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var owner in owners)
            {
                string text;
                try { text = File.ReadAllText(owner); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                if (text.Contains($"URL=\"../{name}\"", StringComparison.OrdinalIgnoreCase)
                    || text.Contains($"Name=\"{name}\"", StringComparison.OrdinalIgnoreCase))
                    return text.Contains("NI.Lib.Icon", StringComparison.Ordinal) ? owner : null;
            }
        }
        return null;
    }

    /// <summary>
    /// Draw and set the icon, never throwing: a failed icon is reported in the step and does not
    /// fail the tool - the VI is written and sound either way.
    /// </summary>
    internal static async Task<JsonObject> ApplyAsync(LvaiConnection connection, string viPath,
        string? requested, Kind kind, string? owner, string? backend, int timeoutSeconds,
        CancellationToken ct)
    {
        var step = new JsonObject { ["step"] = "icon", ["viPath"] = viPath };
        if (requested is { } r && r.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            step["ok"] = true;
            step["skipped"] = "icon: \"none\" was passed";
            return step;
        }
        if (!File.Exists(viPath))
        {
            step["ok"] = false;
            step["skipped"] = "no VI on disk";
            return step;
        }
        if (OwnerWithIcon(viPath) is { } iconOwner)
        {
            step["ok"] = true;
            step["skipped"] = "ownerIconPresent";
            step["owner"] = iconOwner;
            step["note"] = "Its class or library has its own icon, which a member carries as a template " +
                           "layer; drawing one would flatten it. Use lvai_set_owner_icon for the owner.";
            return step;
        }

        var lines = requested is { Length: > 0 } ? Parse(requested) ?? Derive(viPath, kind, owner)
                                                 : Derive(viPath, kind, owner);
        step["lines"] = new JsonArray(lines.L1, lines.L2, lines.L3);
        step["derived"] = requested is not { Length: > 0 };
        try
        {
            var compiledBefore = BulkTools.ContainsCompiledCode(viPath);
            var answer = await (Apply?.Invoke(viPath, lines.L1, lines.L2, lines.L3, backend, timeoutSeconds, ct)
                ?? new IconTools(connection).SetViIconAsync(viPath, line1: lines.L1, line2: lines.L2,
                       line3: lines.L3, timeoutSeconds: timeoutSeconds, ct: ct, backend: backend));
            var parsed = JsonNode.Parse(answer) as JsonObject;
            var ok = parsed?["ok"]?.GetValue<bool>() == true;
            step["ok"] = ok;
            if (!ok) step["answer"] = parsed;
            // THE ICON SAVE BRINGS COMPILED CODE IN (EventDataFieldTools records it), so a VI that
            // was source-only goes back to source-only.
            if (ok && !compiledBefore && BulkTools.ContainsCompiledCode(viPath))
            {
                var strip = await (Strip?.Invoke(viPath, timeoutSeconds, ct)
                    ?? new DiagramCleanup(connection).StripCompiledAsync(viPath, timeoutSeconds, ct));
                step["stripCompiled"] = !BulkTools.ContainsCompiledCode(viPath);
                if (step["stripCompiled"]!.GetValue<bool>() is false) step["stripAnswer"] = strip;
            }
        }
        catch (Exception e)
        {
            step["ok"] = false;
            step["error"] = e.Message;
        }
        if (step["ok"]?.GetValue<bool>() != true)
            step["note"] = "THE ICON WAS NOT SET - the VI is written and otherwise unaffected. Set it with " +
                           "lvai_set_vi_icon; a VI with LabVIEW's blank icon is not finished.";
        return step;
    }

    /// <summary>Append the icon step to a tool answer that has `steps`; ok is not changed.</summary>
    internal static string AppendTo(string answer, JsonObject step)
    {
        if (JsonNode.Parse(answer) is not JsonObject o) return answer;
        if (o["steps"] is JsonArray steps) steps.Add(step);
        else o["icon"] = step;
        if (step["ok"]?.GetValue<bool>() != true)
            o["note"] = (o["note"]?.GetValue<string>() ?? "") + " " + step["note"]?.GetValue<string>();
        return Json.Document(o);
    }

    /// <summary>Test seam for the icon set (viPath, l1, l2, l3, backend); null in production.</summary>
    internal static Func<string, string, string?, string?, string?, int, CancellationToken, Task<string>>? Apply { get; set; }

    /// <summary>Test seam for the compiled-code strip; null in production.</summary>
    internal static Func<string, int, CancellationToken, Task<JsonObject>>? Strip { get; set; }

    private static string? At(List<string> words, int i) => i < words.Count ? Abbreviate(words[i]) : null;

    /// <summary>Words of a name: split on separators and camelCase, stop words dropped.</summary>
    internal static List<string> Words(string name)
    {
        var bare = name.Contains(':') ? name[(name.LastIndexOf(':') + 1)..] : name;
        bare = Regex.Replace(bare, "(?<=[a-z0-9])(?=[A-Z])", " ");
        return [.. Regex.Split(bare, @"[^A-Za-z0-9]+")
                        .Where(w => w.Length > 0 && !StopWords.Contains(w))];
    }

    /// <summary>Upper-case and at most 5 characters: vowels after the first letter go first.</summary>
    internal static string Abbreviate(string word)
    {
        var w = word.ToUpperInvariant();
        if (w.Length <= IconImage.MaxCharsPerLine) return w;
        var b = new StringBuilder().Append(w[0]);
        foreach (var c in w[1..]) if ("AEIOU".IndexOf(c) < 0) b.Append(c);
        var s = b.ToString();
        return s.Length <= IconImage.MaxCharsPerLine ? s : s[..IconImage.MaxCharsPerLine];
    }
}
