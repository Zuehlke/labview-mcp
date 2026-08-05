using System.Text.Json.Nodes;

namespace LabVIEWMcp.Tests.Support;

/// <summary>
/// Tools return JSON strings. Asserting on parsed values instead of substrings means a
/// formatting change does not break tests, but a contract change does.
/// </summary>
internal static class Res
{
    public static JsonObject Obj(string json) =>
        JsonNode.Parse(json) as JsonObject
        ?? throw new InvalidOperationException($"Not a JSON object: {Trim(json)}");

    public static string Str(string json, string key) => Get(json, key).GetValue<string>();

    public static int Int(string json, string key) => Get(json, key).GetValue<int>();

    public static long Long(string json, string key) => Get(json, key).GetValue<long>();

    public static bool Bool(string json, string key) => Get(json, key).GetValue<bool>();

    public static JsonArray Arr(string json, string key) => Get(json, key).AsArray();

    public static bool Has(string json, string key) => Obj(json).ContainsKey(key);

    public static bool IsNull(string json, string key) => Obj(json)[key] is null;

    private static JsonNode Get(string json, string key) =>
        Obj(json)[key] ?? throw new InvalidOperationException(
            $"Key '{key}' missing or null. Payload: {Trim(json)}");

    private static string Trim(string s) => s.Length > 400 ? s[..400] + "..." : s;

    /// <summary>
    /// Locate a repo-relative file. Anchored on this source file's compile-time path rather
    /// than the output directory, because the build output is not always inside the repo -
    /// a redirected OutDir (used when a running MCP server locks bin/) would otherwise make
    /// a test fail for a reason that has nothing to do with the code.
    /// </summary>
    public static string? FindRepoFile(
        string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        foreach (var anchor in new[] { Path.GetDirectoryName(sourceFile), AppContext.BaseDirectory })
        {
            var directory = string.IsNullOrEmpty(anchor) ? null : new DirectoryInfo(anchor);
            while (directory is not null)
            {
                var candidate = Path.Combine(
                    directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
        }
        return null;
    }
}
