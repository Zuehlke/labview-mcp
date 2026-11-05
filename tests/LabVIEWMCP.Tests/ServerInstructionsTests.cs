using System.IO.Pipes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace LabVIEWMCP.Tests;

/// <summary>
/// The orchestrator brief reaches a plugin user's main session only as the MCP `instructions` of
/// the initialize answer - the plugin ships no CLAUDE.md. These tests pin that the answer carries
/// it, over a real MCP session, and that it still says what the colleague's ATM build on plugin
/// 2.4.5 did not do: delegate to the agents, write a build contract, test with Caraya.
/// docs/server-instructions.md.
/// </summary>
public class ServerInstructionsTests
{
    [Fact]
    public void EmbeddedBriefIsByteIdenticalToTheFileInDocs()
    {
        var onDisk = Res.FindRepoFile("docs/orchestrator-brief.md");
        Assert.NotNull(onDisk);

        var expected = File.ReadAllText(onDisk!).Replace("\r\n", "\n");
        var embedded = KnowledgeTools.Load(ServerInstructions.ResourceName).Replace("\r\n", "\n");

        Assert.Equal(expected, embedded);
    }

    [Fact]
    public async Task TheInitializeAnswerCarriesTheOrchestratorBrief()
    {
        var instructions = await InitializeAndReadInstructionsAsync();

        Assert.Equal(ServerInstructions.Text, instructions);

        // the agents, by name
        foreach (var agent in new[]
                 {
                     "labview-vi-generator", "labview-vi-editor", "labview-class-generator",
                     "labview-caraya-unit-test", "labview-lunit-unit-test", "labview-doc-generator",
                     "labview-dqmh-module",
                 })
            Assert.Contains($"`{agent}`", instructions);

        // the build contract and the gate that checks it
        Assert.Contains("build-contract.json", instructions);
        Assert.Contains("lvai_check_build_contract mode=plan", instructions);
        Assert.Contains("lvai_check_build_contract mode=verify", instructions);

        // Caraya, as the default
        Assert.Contains("Caraya is the default test framework", instructions);

        // one folder per agent, and projects are the orchestrator's
        Assert.Contains("One agent, one output directory", instructions);
        Assert.Contains("Only you open and close projects", instructions);
    }

    /// <summary>
    /// The brief is sent with EVERY session's initialize, so it has to stay compact: about
    /// 1.5k tokens, taken here as 4 characters per token.
    /// </summary>
    [Fact]
    public void TheBriefStaysCompact()
    {
        var length = ServerInstructions.Text.Length;
        Assert.True(length is > 1500 and < 6000,
                    $"orchestrator brief is {length} chars; budget about 1.5k tokens (< 6000 chars)");
    }

    /// <summary>
    /// A real client-server initialize over in-process pipes, with the server configured by the
    /// same <see cref="ServerInstructions.Configure"/> that Program.cs hands AddMcpServer.
    /// </summary>
    private static async Task<string?> InitializeAndReadInstructionsAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // client -> server
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var serverIn = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        // server -> client
        using var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        using var clientIn = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);

        var options = new McpServerOptions();
        ServerInstructions.Configure(options);

        await using var server = McpServer.Create(
            new StreamServerTransport(serverIn, toClient, "labview-mcp-test"), options);
        var serverRun = server.RunAsync(cts.Token);

        string? instructions;
        await using (var client = await McpClient.CreateAsync(
                         new StreamClientTransport(toServer, clientIn), cancellationToken: cts.Token))
        {
            Assert.Equal("labview-mcp", client.ServerInfo.Name);
            instructions = client.ServerInstructions;
        }

        cts.Cancel();
        try { await serverRun; } catch (OperationCanceledException) { }
        return instructions;
    }
}
