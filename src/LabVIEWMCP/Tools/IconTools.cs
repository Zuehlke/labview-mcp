using System.Buffers.Binary;
using System.ComponentModel;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Setting a VI's icon - the first capability here that is COMPOSED rather than a thin wrapper
/// over one RPC. AIXML cannot carry an icon ("VI icon graphics" is on NI's not-supported list),
/// so this generates the VI Server helper from scripts\lvdoc_set_icon.xml, runs it, and then
/// verifies the outcome from the filesystem - because the helper's own return value is
/// unusable: RunVIAsTopLevel cannot marshal its indicators back and reports errorCode 91
/// after the VI has already done its work. The recipe and the measurements behind it are in
/// docs/vi-server-reference.md, "Writing back: setting a VI's icon".
/// </summary>
[McpServerToolType]
internal sealed class IconTools(LvaiConnection connection)
{
    /// <summary>Name of the helper's AIXML source inside the scripts folder.</summary>
    internal const string HelperAixmlFileName = "lvdoc_set_icon.xml";

    [McpServerTool(Name = "lvai_set_vi_icon", Destructive = true, OpenWorld = true,
                   Title = "Set a VI's icon from an image file")]
    [Description("""
        MUTATING: replaces the icon of an EXISTING .vi and saves the VI in place.
        lvai_convert_aixml_to_vi cannot do this - "VI icon graphics" is on NI's not-supported
        list - so this composes the VI Server route instead: generate the helper VI from
        scripts\lvdoc_set_icon.xml (once, then reused), run it, read the icon back out.
        Measured on LabVIEW 2026: a 32x32 PNG is applied as-is and the icon read back out of
        the VI is pixel-identical. Other formats and sizes are untested.
        CALL THIS LAST. lvai_convert_aixml_to_vi over an existing path DESTROYS the icon -
        measured on two VIs - so re-apply after every regeneration. It is safe in the other
        direction: setting an icon does not leave the VI in memory, so the path can still be
        regenerated afterwards.
        DO NOT judge the result by errorCode: 91 with empty outputs is the known
        RunVIAsTopLevel read-back artifact and appears on success. Use the `verified` field,
        and look at the read-back PNG to see what actually landed in the VI.
        """)]
    public async Task<string> SetViIconAsync(
        [Description(@"Absolute path to the .vi whose icon is replaced - it is saved in place")]
        string viPath,
        [Description(@"Absolute path to the icon image. A 32x32 PNG is what was measured")]
        string iconImagePath,
        [Description("""
            Where to write the icon read back OUT of the VI afterwards, for verification.
            Defaults to a temp file. Its directory is created if missing - LabVIEW's own file
            write does not create directories and fails with Error 7 instead.
            """)]
        string? readBackPath = null,
        [Description("""
            Where to keep the generated helper VI. Defaults to a per-user cache directory,
            because the scripts folder next to the exe may be read-only. Generated once and
            reused; pass regenerateHelper to force a rebuild.
            """)]
        string? helperViPath = null,
        [Description("""
            The helper's AIXML source. Defaults to lvdoc_set_icon.xml inside the folder
            lvai_status reports as scriptsDirectory.
            """)]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(viPath))
                throw new FileNotFoundException($"No VI at '{viPath}'.", viPath);
            if (!File.Exists(iconImagePath))
                throw new FileNotFoundException($"No icon image at '{iconImagePath}'.", iconImagePath);

            var aixml = helperAixmlPath ?? DefaultHelperAixmlPath()
                ?? throw new FileNotFoundException(
                    $"The helper's AIXML source could not be located: no scripts folder next to " +
                    $"the exe (lvai_status reports it as scriptsDirectory). Pass helperAixmlPath " +
                    $"explicitly, pointing at {HelperAixmlFileName}.");
            if (!File.Exists(aixml))
                throw new FileNotFoundException($"No helper AIXML at '{aixml}'.", aixml);

            var helperVi = Path.GetFullPath(helperViPath ?? DefaultHelperViPath());
            var readBack = Path.GetFullPath(readBackPath ?? DefaultReadBackPath(viPath));
            EnsureDirectoryOf(helperVi);
            EnsureDirectoryOf(readBack);

            var helperGenerated = false;
            if (regenerateHelper || !File.Exists(helperVi))
            {
                if (await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct)
                    is { } generationFailure) return generationFailure;
                helperGenerated = true;
            }

            var viBefore = Snapshot(viPath);
            var runStartedUtc = DateTime.UtcNow;

            var request = new RunVIAsTopLevelRequest { ViPath = helperVi };
            request.Inputs["VI Path"] = Path.GetFullPath(viPath);
            request.Inputs["Icon File Path"] = Path.GetFullPath(iconImagePath);
            request.Inputs["Read Back Path"] = readBack;

            var response = await connection.InvokeAsync((c, t) =>
                c.RunVIAsTopLevelAsync(request,
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

            var viAfter = Snapshot(viPath);
            var readBackAfter = Snapshot(readBack);

            // The helper writes the read-back file LAST, after Set VI Icon from File and
            // Save:Instrument, so a file written during this call is the one honest signal
            // that all three steps ran. The VI's own bytes are NOT that signal: an icon
            // identical to the one already there leaves the VI unmodified and unsaved.
            var verified = readBackAfter.Exists
                        && readBackAfter.WriteUtc >= runStartedUtc.AddSeconds(-2);

            var warnings = new JsonArray();
            var iconSize = PngSize(iconImagePath);
            if (iconSize is null)
                warnings.Add("The icon image is not a PNG. Only 32x32 PNG has been measured.");
            else if (iconSize != "32x32")
                warnings.Add($"The icon image is {iconSize}, not 32x32. LabVIEW icons are 32x32; " +
                             "whether it scales or crops has not been measured.");
            if (!verified)
                warnings.Add("No read-back file was written, so the icon was probably NOT applied. " +
                             "Check errorMessage, and that the VI is not read-only or locked by a library.");

            return Json.Message(response,
                ("verified", JsonValue.Create(verified)),
                ("helperViPath", JsonValue.Create(helperVi)),
                ("helperAixmlPath", JsonValue.Create(Path.GetFullPath(aixml))),
                ("helperGenerated", JsonValue.Create(helperGenerated)),
                ("viBytesBefore", JsonValue.Create(viBefore.Bytes)),
                ("viBytesAfter", JsonValue.Create(viAfter.Bytes)),
                ("viResaved", JsonValue.Create(viAfter.WriteUtc != viBefore.WriteUtc)),
                ("readBackPath", JsonValue.Create(readBack)),
                ("readBackBytes", JsonValue.Create(readBackAfter.Bytes)),
                ("readBackSize", JsonValue.Create(readBackAfter.Exists ? PngSize(readBack) : null)),
                ("iconImageSize", JsonValue.Create(iconSize)),
                ("warnings", warnings),
                ("note", JsonValue.Create(
                    "errorCode 91 with empty outputs is expected and does NOT mean failure - " +
                    "RunVIAsTopLevel cannot read this helper's indicators back. Judge by " +
                    "`verified`, and open readBackPath to see the icon now stored in the VI.")));
        });

    /// <summary>
    /// Validate then generate the helper VI. Returns null on success, or a ready-made error
    /// payload - the two failures here need different advice than a generic RPC error, and
    /// Error 1051 in particular is unrecoverable without changing the target name.
    /// </summary>
    private async Task<string?> GenerateHelperAsync(
        string aixml, string helperVi, int timeoutSeconds, CancellationToken ct)
    {
        // The shipped helper is known-good, but it is a plain file a user can edit, so the
        // repository's own rule applies: validate before converting. It is the cheap path.
        var validation = await connection.InvokeAsync((c, t) =>
            c.ValidateAIXMLAsync(new ValidateAIXMLRequest { AiXMLFilePath = aixml },
                deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

        if (validation.ErrorCode != 0)
            return Json.Error("helperAixmlInvalid",
                $"The helper AIXML at '{aixml}' does not validate: {validation.ErrorMessage}",
                new { aiXmlPath = Path.GetFullPath(aixml), errorCode = validation.ErrorCode });

        var generation = await connection.InvokeAsync((c, t) =>
            c.ConvertAIXMLToVIAsync(new ConvertAIXMLToVIRequest
            {
                AiXMLFilePath = aixml,
                ViPath = helperVi,
                OpenVI = false,
            }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

        if (generation.ErrorCode == 0 && File.Exists(helperVi)) return null;

        return Json.Error("helperGenerationFailed",
            $"Could not generate the helper VI at '{helperVi}': {generation.ErrorMessage}",
            new
            {
                helperViPath = helperVi,
                errorCode = generation.ErrorCode,
                viExistsNow = File.Exists(helperVi),
                hint = generation.ErrorCode switch
                {
                    1051 => "Error 1051 means a VI of that name is already in LabVIEW's memory - " +
                            "and a failed generation leaves the name occupied for the rest of the " +
                            "session. Pass a different helperViPath, or restart LabVIEW.",
                    // Not the documented "directory does not exist" case: this tool creates the
                    // directory. Measured under %LOCALAPPDATA%, where Save:Instrument refuses the
                    // location itself while %TEMP% accepts it.
                    7 => "Error 7 is LabVIEW refusing to save into " +
                         $"'{Path.GetDirectoryName(helperVi)}'. The directory does exist - this " +
                         "tool creates it - so the location itself is being refused; that has been " +
                         "measured under %LOCALAPPDATA%. Pass helperViPath somewhere else, " +
                         "somewhere under %TEMP% for instance.",
                    _ => null,
                },
            });
    }

    private static void EnsureDirectoryOf(string filePath)
    {
        if (Path.GetDirectoryName(filePath) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
    }

    private static string? DefaultHelperAixmlPath() =>
        StatusTools.ScriptsDirectory() is { } scripts
            ? Path.Combine(scripts, HelperAixmlFileName)
            : null;

    /// <summary>
    /// Under TEMP rather than in the scripts folder, which an install below Program Files cannot
    /// write to - and the generated helper is a build artifact, not a shipped file.
    ///
    /// Measured, and the reason this is NOT %LOCALAPPDATA%: LabVIEW's Save:Instrument fails with
    /// Error 7 "File not found" when generating into %LOCALAPPDATA%\LabVIEWMCP\helpers - twice,
    /// minutes apart, with the directory present and writable by another process - while the very
    /// same call into %TEMP%\LabVIEWMCP\helpers and into C:\Temp succeeds. The cause is not
    /// explained; the only difference observed is that a directory under %LOCALAPPDATA% inherits
    /// an AppContainer ACE which TEMP does not. Error 7 therefore carries its own hint below.
    /// </summary>
    private static string DefaultHelperViPath() =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvdoc_set_icon.vi");

    private static string DefaultReadBackPath(string viPath) =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
            $"{Path.GetFileNameWithoutExtension(viPath)}-icon-readback.png");

    private readonly record struct FileFacts(bool Exists, long Bytes, DateTime WriteUtc);

    private static FileFacts Snapshot(string path) =>
        File.Exists(path)
            ? new FileFacts(true, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path))
            : new FileFacts(false, 0, default);

    /// <summary>
    /// "WxH" from a PNG's IHDR chunk, or null for anything that is not a PNG. The first 24
    /// bytes carry both, which is why reporting icon dimensions needs no imaging dependency.
    /// </summary>
    private static string? PngSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[24];
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length)
                return null;

            ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            if (!head.AsSpan(0, 8).SequenceEqual(signature)) return null;

            var width = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(20, 4));
            return $"{width}x{height}";
        }
        catch (IOException)
        {
            return null;
        }
    }
}
