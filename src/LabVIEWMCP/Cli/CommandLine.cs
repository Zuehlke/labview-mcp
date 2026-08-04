namespace LabVIEWMcp.Cli;

/// <summary>
/// Argument parsing for the CLI side-modes. Deliberately tiny and dependency-free, but
/// extracted from Program so the flag/value edge cases (missing value, a flag following a
/// flag, a non-numeric port) are actually covered by tests instead of assumed.
/// </summary>
internal static class CommandLine
{
    public static bool HasFlag(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The token after <paramref name="name"/>, or null when absent or when the next token is
    /// itself a flag. "--vi --selftest" must not treat "--selftest" as the VI path.
    /// </summary>
    public static string? StringArg(string[] args, string name)
    {
        var index = Array.FindIndex(args,
            a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        if (index < 0 || index + 1 >= args.Length) return null;

        var candidate = args[index + 1];
        return candidate.StartsWith("--", StringComparison.Ordinal) ? null : candidate;
    }

    public static int? IntArg(string[] args, string name) =>
        int.TryParse(StringArg(args, name), out var value) ? value : null;
}
