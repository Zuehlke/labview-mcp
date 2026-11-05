using LabVIEWMcp.Tools;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The MCP <c>instructions</c> the server answers in <c>initialize</c>: the orchestrator brief,
/// <c>docs/orchestrator-brief.md</c>, embedded verbatim. It is the one channel that reaches a
/// plugin user's MAIN session - the plugin ships agents, hooks and <c>.mcp.json</c> but no
/// CLAUDE.md, so "delegate to the agents, write a build contract, Caraya by default" was invisible
/// there (docs/server-instructions.md). Kept in step with CLAUDE.md "Agents first".
/// </summary>
internal static class ServerInstructions
{
    internal const string ResourceName = "orchestrator-brief.md";

    /// <summary>The brief as served; read once per process like every embedded document.</summary>
    internal static string Text => KnowledgeTools.Load(ResourceName);

    /// <summary>
    /// Everything the server announces about itself in <c>initialize</c>. Program.cs and the test
    /// that pins the initialize answer both go through here, so they cannot configure it apart.
    /// </summary>
    internal static void Configure(McpServerOptions options)
    {
        options.ServerInfo = new() { Name = "labview-mcp", Version = "0.1.0" };
        options.ServerInstructions = Text;
    }
}
