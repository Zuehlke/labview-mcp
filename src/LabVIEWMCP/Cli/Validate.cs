using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabVIEWMcp.Cli;

/// <summary>
/// Validate one AIXML file from the command line.
///
/// WHY THIS EXISTS, and it is not convenience. Probing an undocumented VI Server method means
/// validating a two-node file and reading errorCode - one second of work. On a station where the AI
/// addon's gRPC service comes up and goes away again inside seconds, that one second is unreachable
/// from an MCP client, because the round trip is longer than the window. Chaining
/// <c>--ensure-labview</c> and <c>--validate</c> inside ONE shell run is what closes the gap, and
/// looping <c>--validate</c> over a directory of probe files is what makes a method sweep affordable.
///
/// It reports an unreachable service instead of throwing, because one unhandled exception would take
/// a whole probe loop with it - which is what happened the first time this mode was used.
/// </summary>
internal static class Validate
{
    public static async Task<int> RunAsync(int? port, string? aiXmlPath, int timeoutSeconds)
    {
        if (aiXmlPath is not { Length: > 0 })
        {
            Console.Error.WriteLine("--validate needs the path of an AIXML file.");
            return 2;
        }

        var full = Path.GetFullPath(aiXmlPath);
        if (!File.Exists(full))
        {
            Console.Error.WriteLine($"No file at '{full}'.");
            return 2;
        }

        try
        {
            await using var connection =
                new LvaiConnection(NullLogger<LvaiConnection>.Instance, port);
            var answer = await connection.InvokeAsync((c, t) =>
                c.ValidateAIXMLAsync(new ValidateAIXMLRequest { AiXMLFilePath = full },
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync,
                CancellationToken.None);

            Console.WriteLine($"errorCode {answer.ErrorCode}");
            if (answer.ErrorMessage is { Length: > 0 })
                Console.WriteLine(answer.ErrorMessage.Trim());

            return answer.ErrorCode == 0 ? 0 : 1;
        }
        catch (Exception failure)
        {
            Console.WriteLine("errorCode -1");
            Console.WriteLine($"unreachable: {FirstLine(failure.Message)}");
            return 3;
        }
    }

    /// <summary>First line of a message, so a multi-line gRPC failure stays one row in a loop.</summary>
    private static string FirstLine(string message) =>
        message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts
            ? parts[0].Trim()
            : message.Trim();
}
