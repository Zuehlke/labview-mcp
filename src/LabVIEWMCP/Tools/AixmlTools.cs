using System.ComponentModel;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// The AIXML round-trip: LabVIEW's textual representation of a block diagram.
/// VI -> XML is read-only; XML -> VI and apply-to-VI create or modify real code on disk.
///
/// The format has no published schema (no XSD ships with the addon), so the practical
/// way to author it is: convert a similar VI first, study the output, then modify it.
/// Nodes carry a 'uid' and wires are expressed as "terminal:uid.terminal" references in
/// the inputs/outputs attributes.
/// </summary>
[McpServerToolType]
internal sealed class AixmlTools(LvaiConnection connection)
{
    [McpServerTool(Name = "lvai_convert_vi_to_aixml", ReadOnly = true,
                   Title = "Convert a VI to AIXML")]
    [Description("""
        RPC ConvertVIToAIXML. Serializes an existing VI into LabVIEW's AIXML text format and
        writes it to aiXmlFilePath. Does NOT modify the VI. With returnContent the XML is also
        returned inline, which is normally what you want when reading code.
        This is the reference path for learning the AIXML dialect before generating any.
        """)]
    public async Task<string> ConvertViToAixmlAsync(
        [Description(@"Absolute path to the source .vi")] string viPath,
        [Description(@"Absolute path of the .xml file to write")] string aiXmlFilePath,
        [Description("Also return the written XML inline")] bool returnContent = true,
        [Description("Truncate inline content to this many characters (0 = unlimited)")]
        int maxContentChars = 60000,
        [Description("Local budget in seconds")] int timeoutSeconds = 180,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var response = await connection.InvokeAsync((c, t) =>
                c.ConvertVIToAIXMLAsync(new ConvertVIToAIXMLRequest
                {
                    ViPath = viPath,
                    AiXMLFilePath = aiXmlFilePath,
                }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

            return Json.Message(response, await FileFactsAsync(
                aiXmlFilePath, returnContent, maxContentChars, ct));
        });

    [McpServerTool(Name = "lvai_validate_aixml", ReadOnly = true, Title = "Validate an AIXML file")]
    [Description("""
        RPC ValidateAIXML. Asks LabVIEW whether an AIXML file is well-formed and semantically
        acceptable, WITHOUT creating anything. Always run this before lvai_convert_aixml_to_vi
        or lvai_apply_aixml_to_vi - it is the cheap failure path.
        Reading the messages: "Unsupported SubVI: X" means the Call target cannot be resolved
        (project-local subVIs and Express VIs never can); "Object terminal not found for
        input" means a misspelled terminal name, or fallout from such a Call.
        lvai_aixml_reference has the authoring rules and a verified terminal-name table.
        """)]
    public async Task<string> ValidateAixmlAsync(
        [Description(@"Absolute path to the .xml file to validate")] string aiXmlFilePath,
        [Description("Local budget in seconds")] int timeoutSeconds = 120,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var response = await connection.InvokeAsync((c, t) =>
                c.ValidateAIXMLAsync(new ValidateAIXMLRequest { AiXMLFilePath = aiXmlFilePath },
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            return Json.Message(response);
        });

    [McpServerTool(Name = "lvai_convert_aixml_to_vi", Destructive = true, OpenWorld = true,
                   Title = "Create a VI from AIXML (writes a .vi)")]
    [Description("""
        RPC ConvertAIXMLToVI. MUTATING: creates a real .vi file at viPath from an AIXML file,
        overwriting whatever is there. This is LabVIEW code generation.
        With openVI the new VI is also opened in the IDE.
        Validate the XML first (lvai_validate_aixml) and write to a scratch path until the
        output is what you expect.
        BEFORE authoring AIXML call lvai_aixml_reference - the format has no published schema
        and two rules fail silently: a `uid.terminal` string names a NET (wire), not a
        pointer to an element, and fan-out is expressed by repeating that net string; and
        terminal names are literal LabVIEW labels that must be looked up, not guessed
        (`Increment` -> `x+1`, but `Greater?` -> `x > y?` with spaces).
        The generated VI must be self-contained: a Call to a project-local subVI is rejected.
        """)]
    public async Task<string> ConvertAixmlToViAsync(
        [Description(@"Absolute path to the source AIXML .xml file")] string aiXmlFilePath,
        [Description(@"Absolute path of the .vi to create - WILL BE OVERWRITTEN")] string viPath,
        [Description("Open the created VI in the LabVIEW editor")] bool openVI = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 240,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var existedBefore = File.Exists(viPath);
            var response = await connection.InvokeAsync((c, t) =>
                c.ConvertAIXMLToVIAsync(new ConvertAIXMLToVIRequest
                {
                    AiXMLFilePath = aiXmlFilePath,
                    ViPath = viPath,
                    OpenVI = openVI,
                }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

            return Json.Message(response,
                ("viPath", JsonValue.Create(Path.GetFullPath(viPath))),
                ("viExisted", JsonValue.Create(existedBefore)),
                ("viExistsNow", JsonValue.Create(File.Exists(viPath))),
                ("viBytes", JsonValue.Create(File.Exists(viPath) ? new FileInfo(viPath).Length : 0)));
        });

    [McpServerTool(Name = "lvai_apply_aixml_to_vi", Destructive = true, OpenWorld = true,
                   Title = "Apply AIXML to an existing VI (modifies it)")]
    [Description("""
        RPC ApplyAIXMLToVI. MUTATING: applies an AIXML description onto an EXISTING VI,
        changing its block diagram. This is the RPC behind LabVIEW's AI code completion.
        There is no undo through this interface - keep a copy of the VI, or work on a copy.
        """)]
    public async Task<string> ApplyAixmlToViAsync(
        [Description(@"Absolute path to the .vi to modify")] string viPath,
        [Description(@"Absolute path to the AIXML .xml describing the change")] string aiXmlFilePath,
        [Description("Local budget in seconds")] int timeoutSeconds = 240,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var before = File.Exists(viPath) ? new FileInfo(viPath).Length : 0;
            var response = await connection.InvokeAsync((c, t) =>
                c.ApplyAIXMLToVIAsync(new ApplyAIXMLToVIRequest
                {
                    ViPath = viPath,
                    AiXMLFilePath = aiXmlFilePath,
                }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

            return Json.Message(response,
                ("viBytesBefore", JsonValue.Create(before)),
                ("viBytesAfter", JsonValue.Create(
                    File.Exists(viPath) ? new FileInfo(viPath).Length : 0)),
                ("note", JsonValue.Create(
                    "A byte size that did not change may simply mean LabVIEW has the VI open " +
                    "in memory and has not saved it yet.")));
        });

    private static async Task<(string, JsonNode?)[]> FileFactsAsync(
        string path, bool includeContent, int maxChars, CancellationToken ct)
    {
        if (!File.Exists(path))
            return [("xmlWritten", JsonValue.Create(false))];

        var info = new FileInfo(path);
        var facts = new List<(string, JsonNode?)>
        {
            ("xmlWritten", JsonValue.Create(true)),
            ("xmlPath", JsonValue.Create(info.FullName)),
            ("xmlBytes", JsonValue.Create(info.Length)),
        };

        if (includeContent)
        {
            var text = await File.ReadAllTextAsync(path, ct);
            var truncated = maxChars > 0 && text.Length > maxChars;
            facts.Add(("xmlTruncated", JsonValue.Create(truncated)));
            facts.Add(("xml", JsonValue.Create(truncated ? text[..maxChars] : text)));
        }

        return [.. facts];
    }
}
