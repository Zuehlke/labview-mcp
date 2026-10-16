using System.ComponentModel;
using System.Globalization;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// What LabVIEW's Error List shows - every BROKEN VI in memory with the reason it is broken.
///
/// WHY THIS EXISTS. <c>lvai_exec_state</c> says a VI is eBad and names a missing subVI, and nothing
/// else in the toolset said WHY: a loose wire, an unwired required input and a type conflict all read
/// as eBad and nothing more, so the user found the cause in the IDE's Error List while every answer
/// here was silent (the DQMH handler of 2026-10-06, a loose wire in ATM Main.vi on 2026-10-14). Two
/// VI Server methods the public catalogue does not list close that: <c>{LV.Application}</c>
/// <c>Get Broken VI List</c> and <c>{LV.VI}</c> <c>Get Errors</c>. NI's Application Builder uses the
/// pair to explain a failed build; their spellings were read off that VI.
///
/// THE HELPER RUNS OVER ActiveX, AND THAT IS THE WHOLE TRICK. Measured 2026-10-14 as an A/B with one
/// broken VI open in the IDE: run over ActiveX - which runs a VI in the IDE's MAIN application
/// instance - the list held it with <c>Add: Contains unwired or bad terminal</c>; run through NI's
/// <c>RunVIAsTopLevel</c>, in the AI addon's own instance, the same helper answered an empty list with
/// no error. A VI opened inside a project lives in that project's instance, so the helper reads the
/// ACTIVE project's instance as well.
///
/// The helper is COMPOSED by our own composer, whose parts for both methods were harvested from
/// NI's own AB_Get_Detailed_BrokenVI_Message.vi - so no NI service is needed. NI's converter is the
/// fallback; both builds were measured listing the same broken VI with the same line.
/// </summary>
[McpServerToolType]
internal sealed class ErrorListTools(LvaiConnection connection)
{
    internal const string HelperAixmlFileName = "lvai_vi_errors.xml";

    /// <summary>The helper's indicators, read back over ActiveX.</summary>
    internal static readonly string[] HelperReadBack =
    [
        "names xml", "errors xml", "details xml", "per vi error xml", "code", "source",
        "project path", "project names xml", "project errors xml", "project details xml",
        "project per vi error xml", "project code", "project source",
    ];

    /// <summary>LabVIEW's "no project is active" - the project half's ordinary empty answer.</summary>
    internal const int NoActiveProject = 1055;

    /// <summary>The fixed name our validations leave in memory - see docs/scratch-vis-in-memory.md.</summary>
    internal const string ScratchValidatePrefix = "LVMCP Validate";

    [McpServerTool(Name = "lvai_error_list", ReadOnly = true, OpenWorld = true,
                   Title = "What does LabVIEW's Error List say?")]
    [Description("""
        READ-ONLY: what LabVIEW's Error List window shows - every BROKEN VI in memory, each with the
        Error List's own lines (`Add: Contains unwired or bad terminal`, `Wire: Wire has loose ends.`)
        and the explanation the dialog prints under them. lvai_exec_state says THAT a VI is broken;
        this says WHY, which nothing else here does for a loose wire, an unwired required input or a
        type conflict.
        TWO APPLICATION INSTANCES ARE READ: the IDE's main one, and the ACTIVE PROJECT's, because a
        VI opened inside a project lives there. `instances` reports each; the project one answers
        `available: false` when no project is active, which is not a failure.
        IT SEES WHAT IS IN MEMORY, NOT WHAT IS ON DISK. A broken VI nobody has open is not listed, and
        the in-memory copy is what the IDE shows - an unsaved edit included. To ask about a file on
        disk, open it first with lvai_open_file, then call this.
        `viName` narrows the answer to one VI (a name or a path; the file name is matched).
        NEEDS A RUNNING LabVIEW, not NI's service: the helper is composed by our composer (NI's
        converter is the fallback) and run over LabVIEW's ActiveX server, which runs it in the
        IDE's main instance - through NI's service it would run in the addon's own instance and see
        nothing, measured. Warnings are not reported: the methods return errors only.
        lvai_exec_state names the same lines for ONE VI read from disk, which every tool that
        checks executability now forwards as `errors`.
        """)]
    public async Task<string> ErrorListAsync(
        [Description("Only this VI: a VI name or a path - its file name is matched, case-insensitively")]
        string? viName = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 120,
        CancellationToken ct = default)
    {
        if (LabViewCom.NotReady() is { } notReady)
            return Json.Error("labviewNotRunning", notReady);

        var (helper, problem) = await EnsureHelperAsync(regenerateHelper, timeoutSeconds, ct);
        if (helper is null)
            return Json.Error("helperUnavailable",
                "The Error List helper could not be generated, so nothing is known about any VI. " +
                "That is not a clean bill of health. " + problem);

        LabViewCom.RunResult run;
        await HelperGate.WaitAsync(ct);
        try { run = await LabViewCom.RunAsync(helper, [], HelperReadBack, 0, timeoutSeconds, ct); }
        catch (LabViewCom.ComException e)
        {
            return Json.Error(e.Kind, e.Message, new { helperViPath = helper, via = "ActiveX" });
        }
        finally { HelperGate.Release(); }

        var (list, notTrusted) = FromHelperRun(run);
        if (list is null)
            return Json.Error("helperAnswerNotTrusted", notTrusted!, new { helperViPath = helper });

        return Answer(list, viName, helper);
    }

    /// <summary>
    /// One run of the helper at a time in this process. Measured 2026-10-14: two calls issued
    /// together both ran the one helper VI over ActiveX, and the second answered
    /// `0x3E8 The VI is not in a state compatible with this operation`.
    /// </summary>
    private static readonly SemaphoreSlim HelperGate = new(1, 1);

    /// <summary>One broken VI: its name, the Error List's lines paired with their explanations.</summary>
    internal sealed record BrokenVi(string Name, IReadOnlyList<ErrorLine> Errors, int ReadCode,
                                    string ReadSource)
    {
        /// <summary>One of our own scratch validations, which nothing can close.</summary>
        internal bool OurScratch => Name.StartsWith(ScratchValidatePrefix, StringComparison.Ordinal);
    }

    internal sealed record ErrorLine(string Error, string? Detail);

    /// <summary>What one application instance answered.</summary>
    internal sealed record InstanceReading(string Instance, int Code, string Source,
                                           IReadOnlyList<BrokenVi> Vis, string? ProjectPath = null);

    internal sealed record ErrorList(InstanceReading Main, InstanceReading Project);

    /// <summary>
    /// The helper's answer, or null with the reason it is not trusted. Pure, so the parsing and its
    /// rules are tested without LabVIEW.
    /// </summary>
    internal static (ErrorList? List, string? NotTrusted) FromHelperRun(LabViewCom.RunResult run)
    {
        if (run.ExecState == 0)
            return (null, "the lvai_vi_errors helper is itself not executable (execState 0)");
        if (!run.Finished)
            return (null, "the helper did not finish within the budget and was aborted");

        var values = run.Values.GroupBy(v => v.Name, StringComparer.Ordinal)
                               .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        string? Read(string name) =>
            values.TryGetValue(name, out var v) && v.Problem is null ? v.Value : null;

        var unread = HelperReadBack.Where(n => Read(n) is null).ToList();
        if (unread.Count > 0)
            return (null, "the helper's answer could not be read back: " + string.Join(", ", unread));

        try
        {
            var main = Instance("main", Read, "");
            var project = Instance("activeProject", Read, "project ") with
            {
                ProjectPath = Read("project path") is { Length: > 0 } p ? p : null,
            };
            return (new ErrorList(main, project), null);
        }
        catch (Exception e) when (e is System.Xml.XmlException or FormatException)
        {
            return (null, "the helper answered XML this could not read: " + e.Message);
        }
    }

    private static InstanceReading Instance(string instance, Func<string, string?> read, string prefix)
    {
        var names = StringArray(read(prefix + "names xml")!);
        var errors = read(prefix + "errors xml") is { } e ? StringArray(e) : Array.Empty<string>();
        var details = read(prefix + "details xml") is { } d ? StringArray(d) : Array.Empty<string>();
        var readErrors = read(prefix + "per vi error xml") is { } r ? StringArray(r) : Array.Empty<string>();

        var vis = new List<BrokenVi>();
        for (var i = 0; i < names.Count; i++)
        {
            var lines = i < errors.Count ? StringArray(errors[i]) : Array.Empty<string>();
            var explanations = i < details.Count ? StringArray(details[i]) : Array.Empty<string>();
            var (code, source) = i < readErrors.Count ? ErrorCluster(readErrors[i]) : (0, "");
            vis.Add(new BrokenVi(names[i],
                lines.Select((line, j) => new ErrorLine(line, j < explanations.Count ? explanations[j] : null))
                     .ToList(),
                code, source));
        }
        return new InstanceReading(instance,
            int.Parse(read(prefix + "code")!, NumberStyles.Integer, CultureInfo.InvariantCulture),
            Lf(read(prefix + "source")!), vis);
    }

    /// <summary>
    /// A 1D string array as LabVIEW's Flatten To XML writes it. An EMPTY array still carries one
    /// template element, so <c>Dimsize</c> decides how many are real.
    /// </summary>
    internal static IReadOnlyList<string> StringArray(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return [];
        var array = XElement.Parse(xml);
        if (array.Name.LocalName != "Array")
            throw new FormatException($"expected <Array>, got <{array.Name.LocalName}>");
        var size = int.Parse(array.Element("Dimsize")?.Value ?? "0", NumberStyles.Integer,
                             CultureInfo.InvariantCulture);
        return array.Elements("String").Take(size)
                    .Select(s => Lf(s.Element("Val")?.Value ?? "").TrimEnd('\n'))
                    .ToList();
    }

    /// <summary>An error cluster as LabVIEW's Flatten To XML writes it.</summary>
    internal static (int Code, string Source) ErrorCluster(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return (0, "");
        var cluster = XElement.Parse(xml);
        var code = cluster.Elements("I32").FirstOrDefault(e => e.Element("Name")?.Value == "code")
                          ?.Element("Val")?.Value;
        var source = cluster.Elements("String").FirstOrDefault(e => e.Element("Name")?.Value == "source")
                            ?.Element("Val")?.Value;
        return (int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 0,
                Lf(source ?? ""));
    }

    private static string Lf(string text) => text.ReplaceLineEndings("\n");

    /// <summary>Whether <paramref name="name"/> is the VI <paramref name="wanted"/> names.</summary>
    internal static bool Matches(string name, string wanted)
    {
        var file = Path.GetFileName(wanted.Trim());
        return string.Equals(name, file, StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, wanted.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    internal static string Answer(ErrorList list, string? viName, string helper)
    {
        object Vi(BrokenVi vi) => new
        {
            name = vi.Name,
            errors = vi.Errors.Select(e => new { error = e.Error, detail = e.Detail }),
            readErrorCode = vi.ReadCode == 0 ? (int?)null : vi.ReadCode,
            readErrorSource = vi.ReadCode == 0 ? null : vi.ReadSource,
            ourScratchValidation = vi.OurScratch ? true : (bool?)null,
        };

        IEnumerable<BrokenVi> Pick(InstanceReading r) =>
            viName is { Length: > 0 } wanted ? r.Vis.Where(v => Matches(v.Name, wanted)) : r.Vis;

        var main = Pick(list.Main).ToList();
        var projectAvailable = list.Project.Code != NoActiveProject;
        var project = projectAvailable ? Pick(list.Project).ToList() : new List<BrokenVi>();
        var all = main.Concat(project).ToList();

        string note;
        if (viName is { Length: > 0 } && all.Count == 0)
            note = $"'{viName}' is not in the broken list of either instance. Either it is executable, " +
                   "or it is not in memory - this reads memory, not disk. Open it with lvai_open_file " +
                   "and call again, or ask lvai_exec_state, which opens it from disk.";
        else if (all.Count == 0)
            note = "No broken VI in memory" + (projectAvailable ? "" : " (no project active)") +
                   ". That says nothing about VIs nobody has open.";
        else
            note = "Each `error` is a line of LabVIEW's Error List; `detail` is the text the dialog " +
                   "shows under it." +
                   (all.Any(v => v.OurScratch)
                       ? " `ourScratchValidation` marks a VI our own validation left in memory - not " +
                         "the user's code, and safe to discard at LabVIEW's exit."
                       : "");

        return Json.Object(new
        {
            ok = list.Main.Code == 0 && (!projectAvailable || list.Project.Code == 0),
            brokenCount = all.Count,
            viName,
            instances = new object[]
            {
                new
                {
                    instance = "main",
                    code = list.Main.Code,
                    source = list.Main.Code == 0 ? null : list.Main.Source,
                    vis = main.Select(Vi),
                },
                new
                {
                    instance = "activeProject",
                    available = projectAvailable,
                    projectPath = list.Project.ProjectPath,
                    code = list.Project.Code,
                    source = list.Project.Code == 0 ? null : list.Project.Source,
                    vis = project.Select(Vi),
                    note = projectAvailable ? null : "No project is active (1055), so only the main instance was read.",
                },
            },
            via = "ActiveX",
            helperViPath = helper,
            note,
        });
    }

    /// <summary>
    /// The helper VI: composed by our composer first (no NI service needed), NI's converter only
    /// when the composer cannot. Both are rebuilt when the AIXML is newer than the VI.
    /// </summary>
    private async Task<(string? Vi, string? Problem)> EnsureHelperAsync(
        bool regenerate, int timeoutSeconds, CancellationToken ct)
    {
        if (regenerate)
        {
            var stale = Path.Combine(ComposedHelpers.Folder, Path.ChangeExtension(HelperAixmlFileName, ".vi"));
            try { File.Delete(stale); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        var (composed, why) = await ComposedHelpers.EnsureAsync(HelperAixmlFileName, timeoutSeconds, ct);
        if (composed is not null) return (composed, null);
        // NI's converter only where the server's backend is grpc: on a pylabview server NI's
        // service is not the fallback, it is the thing to stay away from (the audit of 2026-10-16)
        if (AixmlBackend.ServerDefault().Name == AixmlBackend.PyLabview)
            return (null, $"The composer could not build it ({why}). The server's backend is pylabview, " +
                          "so NI's converter is not tried.");
        var (generated, problem) = await GenerateWithNiAsync(regenerate, timeoutSeconds, ct);
        return generated is not null
            ? (generated, null)
            : (null, $"The composer could not build it ({why}), and neither could NI's converter: {problem}");
    }

    private async Task<(string? Vi, string? Problem)> GenerateWithNiAsync(
        bool regenerate, int timeoutSeconds, CancellationToken ct)
    {
        if (StatusTools.ScriptsDirectory() is not { } scripts
            || !File.Exists(Path.Combine(scripts, HelperAixmlFileName)))
            return (null, $"{HelperAixmlFileName} was not found in the scripts folder beside the server.");
        var aixml = Path.Combine(scripts, HelperAixmlFileName);
        var helperVi = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvai_vi_errors.vi");
        if (!regenerate && !HelperCache.NeedsRebuild(aixml, helperVi)) return (helperVi, null);

        Directory.CreateDirectory(Path.GetDirectoryName(helperVi)!);
        try
        {
            var validation = await connection.InvokeAsync((c, t) =>
                c.ValidateAIXMLAsync(new ValidateAIXMLRequest { AiXMLFilePath = aixml },
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            if (validation.ErrorCode != 0)
                return (null, $"The helper AIXML does not validate: {validation.ErrorMessage}");

            var generation = await connection.InvokeAsync((c, t) =>
                c.ConvertAIXMLToVIAsync(new ConvertAIXMLToVIRequest
                {
                    AiXMLFilePath = aixml,
                    ViPath = helperVi,
                    OpenVI = false,
                }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            return generation.ErrorCode == 0 && File.Exists(helperVi)
                ? (helperVi, null)
                : (null, $"NI's converter could not write the helper: {generation.ErrorMessage}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (null, "NI's service, which generates this helper, did not answer: " + e.Message +
                          " Check lvai_status.");
        }
    }
}
