namespace LabVIEWMcp.Infra;

/// <summary>
/// Which converter turns AIXML into a VI and back: NI's gRPC service (Nigel) or our own pylabview
/// composer (<see cref="AixmlComposer"/>). Both stay in the tree side by side - the user's request
/// of 2026-10-10 - and nothing switches between them on its own.
///
/// PRECEDENCE, highest first:
///   1. the `backend` argument of the call
///   2. `--aixml-backend` on the server's command line
///   3. the LABVIEWMCP_AIXML_BACKEND environment variable
///   4. grpc
///
/// THE SERVER DEFAULT REACHES ONLY A TOOL THE CLIENT CALLED ITSELF. About forty places in this
/// server call lvai_generate_vi or the convert tools from C# - the class tools, the test
/// generators, the malleable route - and they depend on what only NI's converter does: class wires
/// it writes past validation, subVIs it resolves because they are loaded. A server-wide default of
/// pylabview must not reroute those in silence, so for them an omitted backend stays grpc.
/// <see cref="DiagnosingTool"/> records which tool the client invoked in <see cref="ClientTool"/>,
/// and a tool takes the server default only when that is its own name.
/// </summary>
internal static class AixmlBackend
{
    public const string Grpc = "grpc";
    public const string PyLabview = "pylabview";
    public const string Variable = "LABVIEWMCP_AIXML_BACKEND";
    public const string Flag = "--aixml-backend";

    public static readonly string[] Names = [Grpc, PyLabview];

    /// <summary>The MCP tool the client invoked in this async flow; set by DiagnosingTool.</summary>
    public static readonly AsyncLocal<string?> ClientTool = new();

    private static string? _flagValue;

    /// <summary>The backend named on the command line, already checked by <see cref="Normalise"/>.</summary>
    public static void SetFromCommandLine(string name) => _flagValue = name;

    /// <summary>What was chosen, and which of the four rules chose it - every answer says both.</summary>
    internal sealed record Choice(string Name, string Source);

    /// <summary>A known backend name in its canonical spelling, or null.</summary>
    public static string? Normalise(string? value) =>
        value?.Trim().ToLowerInvariant() is { } v && Names.Contains(v) ? v : null;

    /// <summary>
    /// The backend for a call to <paramref name="tool"/>. False, with the refusal in
    /// <paramref name="error"/>, when a name somewhere in the chain is not a backend - an unknown
    /// value is refused rather than ignored, because falling back to grpc would answer a question
    /// nobody asked.
    /// </summary>
    public static bool TryResolve(string? perCall, string tool, out Choice choice, out string error) =>
        TryResolve(perCall, tool, ClientTool.Value, _flagValue,
                   Environment.GetEnvironmentVariable(Variable), out choice, out error);

    /// <summary>The same, with every input explicit - this is what the tests exercise.</summary>
    internal static bool TryResolve(string? perCall, string tool, string? clientTool,
                                    string? flagValue, string? environmentValue,
                                    out Choice choice, out string error)
    {
        choice = new Choice(Grpc, "default");
        error = "";

        if (!string.IsNullOrWhiteSpace(perCall))
        {
            if (Normalise(perCall) is not { } named)
            {
                error = $"backend '{perCall}' is not one of: {string.Join(", ", Names)}.";
                return false;
            }
            choice = new Choice(named, "call");
            return true;
        }

        if (!string.Equals(clientTool, tool, StringComparison.Ordinal))
        {
            choice = new Choice(Grpc, "internal call - the server default reaches only a tool the client invoked");
            return true;
        }

        if (!string.IsNullOrWhiteSpace(flagValue))
        {
            choice = new Choice(Normalise(flagValue) ?? Grpc, Flag);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            if (Normalise(environmentValue) is not { } fromEnvironment)
            {
                error = $"{Variable}='{environmentValue}' is not one of: {string.Join(", ", Names)}. " +
                        "Fix the variable in the server's configuration, or pass backend on the call.";
                return false;
            }
            choice = new Choice(fromEnvironment, Variable);
            return true;
        }

        return true;
    }

    /// <summary>What a client is told when it names no backend: the server default, for status tools.</summary>
    public static Choice ServerDefault()
    {
        if (!string.IsNullOrWhiteSpace(_flagValue)) return new Choice(_flagValue, Flag);
        var environment = Environment.GetEnvironmentVariable(Variable);
        if (!string.IsNullOrWhiteSpace(environment))
            return new Choice(Normalise(environment) ?? $"invalid: {environment}", Variable);
        return new Choice(Grpc, "default");
    }

    /// <summary>The refusal for an unknown backend, in the shape every tool error has.</summary>
    public static string Refusal(string error) =>
        Json.Error("unknownBackend", error, new { accepted = Names });

    /// <summary>The parameter text, shared so the four tools describe it alike.</summary>
    public const string ParameterDescription = """
        Which route does the work: "grpc" (NI's service inside LabVIEW, the default) or
        "pylabview" (no NI service at all: our own composer and reader write and read a VI with no
        LabVIEW, and what needs a running LabVIEW - executability, a run, a render - goes over
        its ActiveX server). Omit it to use the server's default - --aixml-backend or
        LABVIEWMCP_AIXML_BACKEND, else grpc. Every answer names the backend that ran under
        `backend` and why under `backendSource`. docs/aixml-backends.md has what each covers.
        """;
}
