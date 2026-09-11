using System.Reflection;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Which build of this server is running - the one question a stdio MCP server could not answer
/// about itself until 2026-09-11.
///
/// WHY THIS EXISTS. A colleague reported that the plugin install "behaves differently" from the
/// release zip. Measured that day, the two routes are the same bytes by construction: the
/// marketplace declares the plugin as an `archive` source pointing at
/// releases/latest/download/labview-mcp.zip, so the store install IS that asset unpacked -
/// confirmed file by file, 732 files, every SHA-256 equal. The reported difference was entirely
/// version skew: the marketplace catalogue had not been refreshed since 2026-08-29, so the plugin
/// was serving a copy three releases old.
///
/// Settling that took hashing 800 files across two installs, because nothing could say what it
/// was. All three copies on the machine reported FileVersion 1.0.0 - the SDK default, since the
/// csproj set no Version - and neither plugin.json nor the archive carried a version either.
///
/// What WAS already there is the commit: the .NET SDK appends SourceRevisionId to
/// InformationalVersion, so the shipped exe read `1.0.0+e08bf939...`, and that SHA resolved
/// exactly to the v1.3.0 tag (and the other install's to v1.0.7). So this type is mostly about
/// reading a value that was always present and has never been surfaced.
///
/// Reported by `--version`, by `lvai_status` and by `pylv_status`. That last one matters: it is
/// the only one of the three that answers with no LabVIEW running, which is precisely the state
/// in which someone asks whether their install is intact.
/// </summary>
internal static class ServerVersion
{
    /// <summary>The version stamped at build time, and the commit it was built from.</summary>
    /// <param name="Version">
    /// Three-part version from the release tag, or <c>0.0.0</c> for any build that did not come
    /// from the release workflow.
    /// </param>
    /// <param name="Commit">
    /// Full commit SHA, when the SDK recorded one. Null for a build with no git information -
    /// which is normal for a source archive, and never the case for a published release.
    /// </param>
    /// <param name="IsRelease">
    /// False when <see cref="Version"/> is <c>0.0.0</c>. A dev build is allowed to report itself
    /// as one; what it must not do is pass for a release.
    /// </param>
    internal sealed record Info(string Version, string? Commit, bool IsRelease)
    {
        /// <summary>
        /// One line for a human: "1.3.0 (e08bf939)" or "0.0.0-dev (local build)". Keep the shape
        /// stable - it goes into --version output and into two tool answers.
        /// </summary>
        public string Display =>
            (IsRelease ? Version : Version + "-dev")
            + (Commit is { Length: >= 8 } c ? $" ({c[..8]})" : " (no commit recorded)");
    }

    private static Info? _cached;

    /// <summary>
    /// This assembly's version, read once. Cached because it cannot change within a process and
    /// both status tools call it on every invocation.
    /// </summary>
    public static Info Current => _cached ??= Read(
        typeof(ServerVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>
    /// Split an InformationalVersion into its two halves. Exposed for the tests: the interesting
    /// input shapes are all produced by the build, so there is no other way to exercise them.
    ///
    /// The SDK's format is `<version>+<sha>`, and every part of that is optional in practice -
    /// a build with no git information carries no `+` at all, and a hand-set
    /// InformationalVersion may carry a `-suffix` the release workflow never produces. Anything
    /// unparseable degrades to 0.0.0 rather than throwing: a version reader that can fail is a
    /// status tool that can fail, which is the opposite of the point.
    /// </summary>
    internal static Info Read(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return new Info("0.0.0", null, false);

        var plus = informational.IndexOf('+');
        var version = (plus < 0 ? informational : informational[..plus]).Trim();
        var commit = plus < 0 || plus == informational.Length - 1
            ? null
            : informational[(plus + 1)..].Trim();

        if (version.Length == 0) version = "0.0.0";
        return new Info(version, string.IsNullOrEmpty(commit) ? null : commit,
                        version != "0.0.0");
    }

    /// <summary>
    /// What `--version` prints. The exe path is part of it on purpose: the whole reason this
    /// exists is telling two installs apart, and on a machine with both a plugin copy and a
    /// hand-extracted one the path is what names which of them answered.
    /// </summary>
    public static string CliText()
    {
        var info = Current;
        var lines = new List<string>
        {
            $"LabVIEW MCP {info.Display}",
            $"  version        {info.Version}" + (info.IsRelease ? "" : "   (not a release build)"),
            $"  commit         {info.Commit ?? "(none recorded)"}",
            $"  exe            {Environment.ProcessPath ?? AppContext.BaseDirectory}",
        };

        // VERSION.txt travels in the release archive and names the tag, which the assembly does
        // not: 1.3.0 came from `v1.3.0`, and knowing that saves guessing at the prefix.
        var versionFile = Path.Combine(AppContext.BaseDirectory, "..", "VERSION.txt");
        if (File.Exists(versionFile))
        {
            lines.Add($"  archive        {Path.GetFullPath(versionFile)}");
            foreach (var line in File.ReadAllLines(versionFile))
                if (line.Trim().Length > 0) lines.Add("    " + line.Trim());
        }

        return string.Join(Environment.NewLine, lines);
    }
}
