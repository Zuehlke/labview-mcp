using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Creating a DQMH module template - the result of Tools > DQMH > Module > Create DQMH Module
/// Template, with NO dialog.
///
/// WHY THIS IS NOT A WRAPPER. Delacor's `Create Template Core.vi` ends in a One Button Dialog on
/// SUCCESS and in Simple Error Handler's OK dialog on error - an unconditional modal, and a modal
/// stops the gRPC service. What it does is small, though, and was read off its export on
/// 2026-10-06 (docs/dqmh-scripting.md section 9e): copy the module's folder into
/// `<LabVIEW Data>\DQMH Module Templates\Source\<Relative Location>\<Name>`, and write a metadata
/// file `<Name>.xml` into `...\MetaData`, which `Get Module Type Info.vi` lists as one more module
/// type. So this does those two steps itself.
///
/// NOTHING OF DELACOR'S IS COPIED INTO THIS REPOSITORY. The folders come from running Delacor's
/// own `Template Folders--constant.vi`, and the metadata text from the `Meta Data XML Template`
/// constant in the user's own installed `Create Template Core.vi`, read through its AIXML export
/// at run time - so the file follows whatever DQMH version is installed, and a version whose
/// template no longer has the six `%s` slots measured here is refused rather than guessed at.
/// </summary>
[McpServerToolType]
internal sealed class DqmhTemplateTools(LvaiConnection connection)
{
    private const string TemplateDir = @"project\Delacor\DQMH\_DQMH Create Module Template\";
    internal const string FoldersVi = TemplateDir + "Template Folders--constant.vi";
    internal const string CoreVi = TemplateDir + "Create Template Core.vi";
    internal const string TypeInfoVi = @"project\Delacor\DQMH\_DQMH New Module\Get Module Type Info.vi";

    [McpServerTool(Name = "lvai_dqmh_create_module_template", Destructive = true, OpenWorld = true,
        Title = "Create a DQMH module template")]
    [Description("""
        MUTATING, and it writes into the user's LabVIEW Data folder: turns an existing DQMH module
        into a module TEMPLATE, which Add New DQMH Module then offers as a module type - the result
        of Tools > DQMH > Module > Create DQMH Module Template with NO dialog (Delacor's own VI ends
        in a modal even on success, so this repeats its two steps itself).
        With copyToLabviewData (default) the module's folder is COPIED to
        `<LabVIEW Data>\DQMH Module Templates\Source\<relativeLocation>\<Name>` and the metadata
        refers to that copy; without it nothing is copied and the metadata refers to the module in
        place with absolute paths, as Delacor's own option does. Either way a metadata file
        `<Name>.xml` is written into `...\MetaData`. An existing template of that name is refused
        unless overwrite is true - Delacor's own tool silently wipes it. The answer confirms the new
        type appears in Delacor's module-type catalogue and gives its index.
        """)]
    public async Task<string> CreateModuleTemplateAsync(
        [Description("Absolute path of the module's .lvlib")] string libraryPath,
        [Description("Description shown for the new module type in Add New DQMH Module")]
        string description,
        [Description("Sub-folder under the template Source folder, e.g. 'MyCompany'; may be empty")]
        string relativeLocation = "",
        [Description("Copy the module into LabVIEW Data (Delacor's default); false refers to it in place")]
        bool copyToLabviewData = true,
        [Description("Replace an existing template of the same name")] bool overwrite = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(libraryPath))
                return Json.Error("badArguments", "libraryPath is required.");
            var library = Path.GetFullPath(libraryPath.Trim());
            if (!File.Exists(library) || !library.EndsWith(".lvlib", StringComparison.OrdinalIgnoreCase))
                return Json.Error("fileNotFound", $"No .lvlib at {library}.");
            var relative = (relativeLocation ?? "").Trim().Replace('\\', '/').Trim('/');
            if (RelativeLocationProblem(relative) is { } problem)
                return Json.Error("badArguments", problem);

            var title = Path.GetFileNameWithoutExtension(library);
            var moduleFolder = Path.GetDirectoryName(library)!;
            var tester = Path.Combine(moduleFolder, $"Test {title} API.vi");
            if (!File.Exists(tester))
                return Json.Error("testerMissing",
                    $"No API tester at {tester}. A DQMH template carries its tester, and Delacor's " +
                    "own tool finds it by this name.");

            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return Json.Error("scriptsMissing", "No scripts folder next to the exe.");
            if (DqmhHeadless.TargetPaths([FoldersVi, CoreVi, TypeInfoVi]) is not { } paths)
                return Json.Error("dqmhMissing",
                    "Delacor DQMH is not installed, or this version has no Create Module Template tool.",
                    new { lookedFor = new[] { FoldersVi, CoreVi, TypeInfoVi } });

            var stopwatch = Stopwatch.StartNew();
            var dqmh = new DqmhTools(connection);

            // ---- 1. Delacor's own folders and metadata text ------------------------------------
            var (folders, foldersError) = await dqmh.RunViDetailedAsync(scripts, paths[0], new(),
                timeoutSeconds, ct);
            var metadataFolder = folders is null ? null : DqmhTools.Scalar(folders, "MetaData Folder");
            var sourceFolder = folders is null ? null : DqmhTools.Scalar(folders, "Source Folder");
            if (string.IsNullOrEmpty(metadataFolder) || string.IsNullOrEmpty(sourceFolder))
                return Json.Error("templateFoldersUnreadable",
                    "Template Folders--constant.vi did not answer its two folders.",
                    new { helperError = foldersError });

            var export = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", $"dqmh-template.{Guid.NewGuid():N}.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(export)!);
            var exported = await connection.InvokeAsync((c, t) =>
                c.ConvertVIToAIXMLAsync(new ConvertVIToAIXMLRequest
                {
                    ViPath = paths[1],
                    AiXMLFilePath = export,
                }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            var template = exported.ErrorCode == 0 && File.Exists(export)
                ? MetadataTemplate(File.ReadAllText(export)) : null;
            if (File.Exists(export)) File.Delete(export);
            if (template is null)
                return Json.Error("metadataTemplateUnreadable",
                    "Could not read the `Meta Data XML Template` constant from Create Template Core.vi.",
                    new { exportErrorCode = exported.ErrorCode, exported.ErrorMessage });
            if (Regex.Matches(template, "%s").Count != 6 || template.Replace("%s", "").Contains('%'))
                return Json.Error("metadataTemplateChanged",
                    "This DQMH version's metadata template is not the six-%s template measured on " +
                    "2026-10-06, so it is not filled in by guesswork. Create the template with " +
                    "Delacor's own dialog.");

            // ---- 2. refuse to overwrite unless asked ---------------------------------------------
            var metadataFile = Path.Combine(metadataFolder, title + ".xml");
            var destination = copyToLabviewData
                ? Path.Combine([sourceFolder, .. relative.Split('/', StringSplitOptions.RemoveEmptyEntries), title])
                : null;
            if (!overwrite && (File.Exists(metadataFile)
                               || (destination is not null && Directory.Exists(destination))))
                return Json.Error("templateExists",
                    $"A template named '{title}' already exists. Pass overwrite: true to replace it - " +
                    "its source folder is deleted first, as Delacor's own tool does.",
                    new { metadataFile, sourceFolder = destination });

            // ---- 3. copy, then the metadata ------------------------------------------------------
            if (destination is not null)
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
                CopyDirectory(moduleFolder, destination);
            }
            var values = MetadataValues(title, description ?? "", relative, library, tester,
                copyToLabviewData);
            Directory.CreateDirectory(metadataFolder);
            File.WriteAllText(metadataFile, Fill(template, values).ReplaceLineEndings("\r\n"),
                Encoding.Latin1);

            // ---- 4. does Delacor list it now? ---------------------------------------------------
            var (types, typesError) = await dqmh.RunDetailedAsync(scripts, "lvdqmh_module_types",
                new() { ["type info vi path"] = paths[2] }, timeoutSeconds, ct);
            var catalogue = types is null ? [] : DqmhHeadless.Listed(types, "type strings");
            var index = catalogue.FindIndex(t => string.Equals(t, title, StringComparison.Ordinal));

            // A template copies the module AS IT IS: one made from a broken module gives broken
            // modules - measured 2026-10-06, a template of a Heater whose Main.vi carried a loose
            // #CodeNeeded broadcast call produced a Chiller with Main.vi and tester both execState 0.
            var headless = new DqmhHeadless(connection);
            var mainVi = Path.Combine(moduleFolder, "Main.vi");
            var sourceMainExec = File.Exists(mainVi) ? await headless.ExecStateAsync(mainVi, timeoutSeconds, ct) : null;
            var sourceTesterExec = await headless.ExecStateAsync(tester, timeoutSeconds, ct);
            var sourceWarning = sourceMainExec is 1 && sourceTesterExec is 1 ? null
                : $"The source module is not executable (Main.vi {sourceMainExec?.ToString() ?? "missing"}, " +
                  $"tester {sourceTesterExec?.ToString() ?? "missing"}), and every module made from this " +
                  "template starts out the same. Fix the module and create the template again with overwrite.";

            return Json.Document(new JsonObject
            {
                ["ok"] = index >= 0,
                ["title"] = title,
                ["metadataFile"] = metadataFile,
                ["sourceFolder"] = destination,
                ["copied"] = destination is not null,
                ["moduleTypeIndex"] = index,
                ["catalogue"] = DqmhHeadless.Array(catalogue),
                ["catalogueError"] = typesError,
                ["sourceMainViExecState"] = sourceMainExec,
                ["sourceTesterExecState"] = sourceTesterExec,
                ["warning"] = sourceWarning,
                ["usageNote"] = $"A module made from this template opens {Path.GetFileName(library)} by " +
                    "path; in a project that already holds a library of that name - the source " +
                    "module's own project - Delacor answers 56003. Use the template in another project.",
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                ["note"] = index >= 0
                    ? $"Add New DQMH Module now offers '{title}' as module type {index}. The index " +
                      "belongs to THIS station's catalogue - match by name, never carry the number."
                    : "The files were written but Delacor's catalogue does not list the template - " +
                      "read `catalogue` before using it.",
            });
        });

    // ------------------------------------------------------------------ pure helpers, tested

    /// <summary>
    /// The six values, in the order of the template's `%s` slots as `Create Template Core.vi`
    /// wires them (read 2026-10-06): Title, Description, LocationPath, LibraryPath, TesterPath,
    /// AbsolutePaths. In copy mode the paths are relative to the template's own folder; in place
    /// they are absolute and LocationPath is empty - LabVIEW's Format Into String writes a
    /// boolean as TRUE / FALSE.
    /// </summary>
    internal static string[] MetadataValues(string title, string description, string relative,
        string library, string tester, bool copy) =>
        copy
            ? [title, description, relative.Length > 0 ? $"{relative}/{title}" : title,
               Path.GetFileName(library), Path.GetFileName(tester), "FALSE"]
            : [title, description, "", library, tester, "TRUE"];

    /// <summary>
    /// Format Into String with `%s` only: each slot takes the next value, in order, RAW - Delacor
    /// writes the values unescaped and reads them back by regex rather than with an XML parser
    /// (measured 2026-10-06), so an `&amp;amp;` written here would come back as five characters.
    /// </summary>
    internal static string Fill(string template, IReadOnlyList<string> values)
    {
        var next = 0;
        return Regex.Replace(template, "%s", _ => next < values.Count ? values[next++] : "");
    }

    /// <summary>
    /// The `Meta Data XML Template` constant's text out of an AIXML export, its AIXML escapes
    /// (`\0A`, `\2C`, ...) decoded. Null when the constant is not there.
    /// </summary>
    internal static string? MetadataTemplate(string aixml)
    {
        try
        {
            var value = XDocument.Parse(aixml).Descendants("Constant")
                .FirstOrDefault(c => (string?)c.Attribute("_name") == "Meta Data XML Template")
                ?.Attribute("value")?.Value;
            return value is null ? null
                : Regex.Replace(value, @"\\([0-9A-Fa-f]{2})",
                    m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        }
        catch (System.Xml.XmlException) { return null; }
    }

    /// <summary>
    /// A relative location becomes folder names under the template Source folder and a `/`-split
    /// LocationPath, so it must be a plain relative path.
    /// </summary>
    internal static string? RelativeLocationProblem(string relative)
    {
        if (relative.Length == 0) return null;
        if (relative.Contains(':') || relative.StartsWith('/'))
            return "relativeLocation must be relative - a sub-folder name like 'MyCompany'.";
        foreach (var part in relative.Split('/'))
            if (part.Length == 0 || part is "." or ".."
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return $"relativeLocation has an invalid folder name '{part}'.";
        return null;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
