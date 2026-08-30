using LabVIEWMcp.Infra;

namespace LabVIEWMcp.Cli;

/// <summary>
/// CLI access to the bundled pylabview, so the subprocess path can be exercised WITHOUT an MCP
/// client. That is not a convenience: an MCP client gives up on a call after about a minute and
/// then reports nothing, so a tool that hangs looks identical to a tool that does not exist. This
/// entry point is where such a failure becomes readable - and it is how the interactive-interpreter
/// hang was found, where python received no script argument, started a REPL and blocked on the
/// inherited client pipe.
///
///   LabVIEWMCP --pylv-status
///   LabVIEWMCP --pylv-extract "C:\path\My.vi" --out "C:\out\dir" [--no-annotate]
/// </summary>
internal static class PyLabviewCli
{
    public static int Status()
    {
        var bundle = PyLabview.Locate();
        if (bundle is null)
        {
            Console.Error.WriteLine("pylabview bundle: NOT PROVISIONED");
            Console.Error.WriteLine("  " + PyLabview.NotProvisionedMessage());
            return 1;
        }

        Console.WriteLine($"directory        {bundle.Directory}");
        Console.WriteLine($"python           {bundle.PythonVersion} {bundle.PythonArch}");
        Console.WriteLine($"pylabview        {bundle.PylabviewCommit}");
        Console.WriteLine($"provisioned      {bundle.ProvisionedUtc}");
        Console.WriteLine($"primitive names  {bundle.PrimitiveNamesTsv ?? "(absent)"}");
        Console.WriteLine($"terminal names   {bundle.TerminalNamesTsv ?? "(absent)"}");
        return 0;
    }

    public static async Task<int> ExtractAsync(string? viPath, string? outDirectory, bool annotate)
    {
        if (string.IsNullOrWhiteSpace(viPath) || string.IsNullOrWhiteSpace(outDirectory))
        {
            Console.Error.WriteLine(
                "usage: LabVIEWMCP --pylv-extract <file.vi> --out <directory> [--no-annotate]");
            return 2;
        }

        var bundle = PyLabview.Locate();
        if (bundle is null)
        {
            Console.Error.WriteLine("pylabview bundle: NOT PROVISIONED");
            Console.Error.WriteLine("  " + PyLabview.NotProvisionedMessage());
            return 1;
        }
        if (!File.Exists(viPath))
        {
            Console.Error.WriteLine($"no file at '{viPath}'");
            return 2;
        }

        Directory.CreateDirectory(outDirectory);
        var mainXml = Path.Combine(outDirectory,
            Path.GetFileNameWithoutExtension(viPath).Replace(" ", "") + ".xml");

        var extract = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
            ["-x", "-i", viPath, "-m", mainXml], 180, CancellationToken.None);

        Console.WriteLine($"extract          exit {extract.ExitCode} in {extract.ElapsedMs} ms");
        foreach (var warning in extract.Warnings) Console.WriteLine($"  raw fallback   {warning}");
        if (extract.ExitCode != 0)
        {
            Console.Error.WriteLine(extract.StdErr);
            return 1;
        }

        if (annotate && bundle.AnnotatePy is not null)
        {
            var run = await PyLabview.RunAsync(bundle, bundle.AnnotatePy,
                [outDirectory], 180, CancellationToken.None);
            Console.WriteLine($"annotate         exit {run.ExitCode} in {run.ElapsedMs} ms");
            foreach (var line in run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Console.WriteLine($"  {line.TrimEnd()}");
        }

        var files = Directory.GetFiles(outDirectory);
        Console.WriteLine($"files            {files.Length}");
        Console.WriteLine($"main xml         {Path.GetFullPath(mainXml)}");
        return files.Length == 0 ? 1 : 0;
    }
}
