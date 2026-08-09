namespace LabVIEWMcp.Cli;

/// <summary>
/// Argument parsing for the CLI side-modes. Deliberately tiny and dependency-free, but
/// extracted from Program so the flag/value edge cases (missing value, a flag following a
/// flag, a non-numeric port) are actually covered by tests instead of assumed.
/// </summary>
internal static class CommandLine
{
    /// <summary>
    /// Every flag the CLI understands, and whether it consumes the token after it. This
    /// table is the only thing that makes a mistyped flag detectable, so a new flag in
    /// Program must be added here too - otherwise it is rejected as unknown.
    /// </summary>
    private static readonly Dictionary<string, bool> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--help"] = false,
        ["-h"] = false,
        ["-?"] = false,
        ["--selftest"] = false,
        ["--ensure-labview"] = false,
        ["--dump-schema"] = true, // the file is optional; the value test below handles that
        ["--watch"] = true,
        ["--diagram"] = true,
        ["--corpus"] = true, // the root is optional; the value test below handles that
        ["--port"] = true,
        ["--vi"] = true,
        ["--project"] = true,
        ["--timeout"] = true,
        ["--limit"] = true,
        ["--skip"] = true,
        ["--restart-every"] = true,
        ["--out"] = true,
    };

    /// <summary>
    /// The one description of the modes and flags, printed by --help and by the unknown-flag
    /// guard. Program used to carry the same list as a comment; a single source keeps the
    /// help text from drifting away from what the code actually accepts.
    /// </summary>
    public const string Usage =
        """
        LabVIEW MCP - exposes LabVIEW's private lvai.LVAI gRPC interface as MCP tools.

          LabVIEWMCP                        run as an MCP server over stdio (default)
          LabVIEWMCP --selftest             probe every read-only RPC and print a verdict table
          LabVIEWMCP --dump-schema [file]   print/write the schema the running LabVIEW serves
          LabVIEWMCP --watch <monitor>      wait for inbound LabVIEW events, minutes at a time
          LabVIEWMCP --diagram <vi>         save a VI's rendered block diagram as a PNG
          LabVIEWMCP --corpus [dir]         round-trip every VI in a tree through AIXML
          LabVIEWMCP --ensure-labview       start LabVIEW and wait for its gRPC service

          --port <n>        pin LabVIEW's gRPC port instead of discovering it
          --vi <path>       VI used by --selftest (defaults to a shipped LabVIEW example)
          --project <path>  .lvproj used by --selftest
          --timeout <s>     how long --watch and --ensure-labview wait (default 300),
                            and the per-VI budget for --corpus (default 90)
          --limit <n>       stop --corpus after n VIs
          --skip <a,b>      substrings of a path --corpus must not touch; they are still
                            listed in the results, so the gap stays visible
          --restart-every <n>  --corpus restarts LabVIEW once n projects are open (default 40,
                            0 disables). Nothing in the lvai interface closes a project, so
                            this is the only way to give back what the sweep opens.
                            DESTRUCTIVE: it kills every LabVIEW on the machine.
          --out <path>      output file for --diagram, output directory for --corpus
          --help            print this text

        LABVIEW_GRPC_PORT works instead of --port. The self-test needs LabVIEW 2026 running
        WITH its AI assistant open: the lvai gRPC service starts with Nigel, not with the IDE.
        """;

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

    /// <summary>
    /// Tokens that look like a flag but are not one.
    ///
    /// MEASURED, from issue #7: an unrecognised flag used to be ignored, and the run fell
    /// through to the default mode - the MCP server over stdio, which then blocks reading
    /// stdin forever. One missing hyphen ("-selftest") is therefore indistinguishable from
    /// a hang, on the very first command in the README. Reported now, not ignored.
    /// </summary>
    public static IReadOnlyList<string> UnknownFlags(string[] args)
    {
        var unknown = new List<string>();

        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (!token.StartsWith('-')) continue; // a value or a positional, not our business

            if (!Known.TryGetValue(token, out var takesValue))
            {
                unknown.Add(token);
                continue;
            }

            // Step over the value so "--port -1" does not report "-1" as a flag. The "--"
            // test mirrors StringArg exactly: in "--vi --selftest" the VI has no value and
            // "--selftest" stays a flag, so both halves agree on what a value is.
            if (takesValue && index + 1 < args.Length &&
                !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                index++;
        }

        return unknown;
    }

    /// <summary>
    /// The known flag a mistyped token most likely meant, or null. Compares letters and
    /// digits only, so "-selftest", "--self-test" and "--Self_Test" all reach "--selftest" -
    /// the hyphen mistakes are the ones actually observed in the wild.
    /// </summary>
    public static string? Suggest(string token)
    {
        var wanted = LettersAndDigits(token);
        return wanted.Length == 0
            ? null
            : Known.Keys.FirstOrDefault(known => LettersAndDigits(known) == wanted);
    }

    private static string LettersAndDigits(string token) =>
        new(token.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
