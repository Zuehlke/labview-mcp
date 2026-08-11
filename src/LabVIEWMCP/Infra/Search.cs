namespace LabVIEWMcp.Infra;

/// <summary>
/// Query splitting, shared by every index tool that takes free text.
///
/// It lives here because the same bug was written twice. Both the example index and the palette
/// index passed the WHOLE query to Contains as one literal phrase, and both failed the same way:
/// "waveform" gave 74 example hits while "build waveform array" gave none, and - worse, because
/// it is not an empty answer you would question - "read spreadsheet" returned exactly ONE palette
/// VI, a third-party `MGI Read Spreadsheet File.vi`, while hiding the stock
/// `Read Delimited Spreadsheet.vi` that was the right answer. A confident wrong hit steers a
/// caller into an unnecessary dependency.
///
/// The rule both now use: AND across words, OR across fields. Every word must appear somewhere in
/// the entry; different words may come from different fields.
/// </summary>
internal static class Search
{
    /// <summary>
    /// The query split on whitespace. A single word yields one word, so single-term behaviour is
    /// unchanged; an all-whitespace query yields none and the caller treats that as "no query".
    /// </summary>
    public static IReadOnlyList<string> Words(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries |
                                         StringSplitOptions.TrimEntries);

    /// <summary>True when every word appears in at least one of the fields.</summary>
    public static bool MatchesAll(IReadOnlyList<string> words, params string?[] fields)
    {
        foreach (var word in words)
        {
            var hit = false;
            foreach (var field in fields)
                if (field is not null &&
                    field.Contains(word, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
            if (!hit) return false;
        }
        return true;
    }

    /// <summary>
    /// The line to print when a multi-word query matched nothing - the commonest cause is one
    /// word too many, and saying so is what stops an empty answer being read as "does not exist".
    /// Empty for a single-word query, where the advice would be wrong.
    /// </summary>
    public static string DropAWordHint(IReadOnlyList<string> words) =>
        words.Count > 1
            ? $"All {words.Count} words must appear. Drop the narrowest one and retry - " +
              $"e.g. just \"{words[0]}\"."
            : "";
}
