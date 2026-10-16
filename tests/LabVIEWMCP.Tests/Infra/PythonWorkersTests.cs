using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The resident worker must answer what a python.exe of its own answers. The case that made this
/// a test: `sys.exit("message")`, which the pylv scripts use for every refusal, printed nothing in
/// the first worker - Python prints that message only when the PROCESS ends - and five refusal
/// tests saw an empty stderr. SKIPS where the bundled interpreter or the composer is missing.
/// </summary>
public sealed class PythonWorkersTests
{
    private static readonly (string Name, string Code, string[] Args)[] Scripts =
    [
        ("prints", "import sys\nprint('out', sys.argv[1:])\nprint('err', file=sys.stderr)\n", ["a", "b c"]),
        ("exit-code", "import sys\nprint('before')\nsys.exit(3)\n", []),
        ("exit-message", "import sys\nsys.exit('refused: not a subVI link')\n", []),
        ("crash", "raise ValueError('boom')\n", []),
        ("stdin", "import sys\nprint(repr(sys.stdin.read()))\n", []),
        ("cwd", "import os\nprint(os.path.basename(os.getcwd()))\n", []),
        ("main-guard", "def main():\n    print('main ran')\nif __name__ == '__main__':\n    main()\n", []),
    ];

    [Fact]
    public async Task A_worker_answers_as_a_process_of_its_own_would()
    {
        var bundle = PyLabview.Locate();
        if (bundle is null || AixmlComposer.Locate(out _) is null || !PythonWorkers.Enabled) return;
        var dir = Path.Combine(Path.GetTempPath(), "pyworker-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (name, code, args) in Scripts)
            {
                var script = Path.Combine(dir, name + ".py");
                await File.WriteAllTextAsync(script, code);
                var alone = await PyLabview.RunProcessAsync(bundle, script, args, 60, default);
                var resident = await PythonWorkers.TryRunAsync(bundle, script, args, 60, default);
                Assert.NotNull(resident);
                Assert.Equal((name, alone.ExitCode), (name, resident!.ExitCode));
                Assert.Equal((name, Normalise(alone.StdOut)), (name, Normalise(resident.StdOut)));
                // the traceback's frames differ (the worker's own exec is one of them); its last
                // line, and every line of a plain message, do not
                Assert.Equal((name, LastLine(alone.StdErr)), (name, LastLine(resident.StdErr)));
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_call_past_its_deadline_throws_and_the_next_call_still_runs()
    {
        var bundle = PyLabview.Locate();
        if (bundle is null || AixmlComposer.Locate(out _) is null || !PythonWorkers.Enabled) return;
        var dir = Path.Combine(Path.GetTempPath(), "pyworker-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var slow = Path.Combine(dir, "slow.py");
            await File.WriteAllTextAsync(slow, "import time\ntime.sleep(30)\n");
            var quick = Path.Combine(dir, "quick.py");
            await File.WriteAllTextAsync(quick, "print('still here')\n");

            await Assert.ThrowsAsync<TimeoutException>(() => PythonWorkers.TryRunAsync(bundle, slow, [], 1, default));
            var after = await PythonWorkers.TryRunAsync(bundle, quick, [], 60, default);
            Assert.NotNull(after);
            Assert.Equal("still here", after!.StdOut.Trim());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static string Normalise(string s) => s.Replace("\r\n", "\n").TrimEnd();

    private static string LastLine(string s) =>
        Normalise(s).Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
}
