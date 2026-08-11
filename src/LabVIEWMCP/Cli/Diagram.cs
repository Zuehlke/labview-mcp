using System.Text.Json;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabVIEWMcp.Cli;

/// <summary>
/// Save a VI's block diagram as a PNG.
///
/// GetDescribeVIPromptInfo already returns a rendered picture of the diagram in
/// infoJson.viImage (base64 PNG), but pulling a base64 blob through the MCP transport just
/// to look at it is wasteful. As a CLI mode it lands on disk, where it can be opened - or
/// read by an agent that needs to judge a diagram it just generated.
///
/// This matters for generated code: AIXML carries no coordinates, so LabVIEW decides the
/// entire layout. The only way to find out what a generated VI looks like is to look.
///
/// Usage:
///   LabVIEWMCP --diagram "C:\path\My.vi" --out "C:\temp\my.png"
/// </summary>
internal static class Diagram
{
    public static async Task<int> RunAsync(int? port, string? viPath, string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(viPath))
        {
            Console.Error.WriteLine("--diagram needs a path to a .vi");
            return 2;
        }

        outputPath = string.IsNullOrWhiteSpace(outputPath)
            ? Path.ChangeExtension(Path.GetFileName(viPath), ".png")
            : outputPath;

        var connection = new LvaiConnection(NullLogger<LvaiConnection>.Instance, port);
        await using var _ = connection;

        string? infoJson;
        try
        {
            var client = await connection.GetClientAsync();
            using var call = client.GetDescribeVIPromptInfo(new GetDescribeVIPromptInfoRequest
            {
                ViPath = viPath,
                ViName = "",
                GetNodesInfo = false,      // the picture is what we are after
            }, deadline: Rpc.Deadline(120));

            var (items, reason) = await Rpc.CollectAsync(call.ResponseStream, 1, 90,
                CancellationToken.None);
            if (items.Count == 0)
            {
                Console.Error.WriteLine($"No description returned ({reason}).");
                return 1;
            }
            infoJson = items[0].InfoJson;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Could not describe the VI: {e.Message}");
            return 1;
        }

        using var document = JsonDocument.Parse(infoJson);
        if (!document.RootElement.TryGetProperty("viImage", out var image) ||
            image.GetString() is not { Length: > 0 } base64)
        {
            Console.Error.WriteLine("The description carried no viImage.");
            return 1;
        }

        byte[] png;
        try
        {
            png = Convert.FromBase64String(base64);
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine($"viImage was not valid base64: {e.Message}");
            return 1;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(outputPath, png);

        Console.WriteLine($"{png.Length} bytes -> {Path.GetFullPath(outputPath)}");
        return 0;
    }
}
