using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Resident Python processes for <see cref="PyLabview.RunAsync"/>: each runs
/// scripts\aixml-pylabview\worker.py, which takes one script call after another and answers
/// with its exit code and output, exactly what a fresh <c>python.exe</c> would have printed.
///
/// Why: a pylabview generation started one interpreter for the composer, a second inside it for
/// pylabview's rebuild, one per callee it reads, and two more for the connector-pane check -
/// measured on the LayoutCheck build (2026-10-16) at 1.4-2.3 s a VI for 0.16 s of composing.
/// Resident, with pylabview in-process, the installation's index and every callee read kept, the
/// same composes took 0.35-0.6 s and an extract 0.07 s instead of about 0.5.
///
/// It changes no answer: the same script with the same arguments, the same working directory,
/// stdin closed, stdout and stderr captured apart. A worker that dies or answers nonsense is
/// dropped and the call runs as a subprocess after all, so the old route is the fallback and
/// LABVIEWMCP_PYTHON_WORKER=0 makes it the only one.
/// </summary>
internal static class PythonWorkers
{
    public const string Variable = "LABVIEWMCP_PYTHON_WORKER";
    private const string WorkerScript = "worker.py";

    /// <summary>At most this many run at once; a fifth caller waits for one to come free.</summary>
    private const int MaxWorkers = 4;

    /// <summary>A worker is replaced after this many calls: the composer's caches and the
    /// temporary folders of callees it read grow with every distinct VI it meets.</summary>
    private const int MaxRequests = 300;

    private static readonly SemaphoreSlim Slots = new(MaxWorkers);
    private static readonly object Gate = new();
    private static readonly List<Worker> Idle = [];
    private static readonly List<Worker> All = [];
    private static int _hooked;

    /// <summary>Workers started since the server began - for a status answer and the tests.</summary>
    public static int Started { get; private set; }

    public static bool Enabled =>
        Environment.GetEnvironmentVariable(Variable) is not { } v ||
        !(v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("off", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The call run in a worker, or null when no worker could run it - disabled, worker.py not
    /// found, or the worker died on it - and the caller runs a subprocess instead. A call that
    /// outruns its deadline kills its worker and throws, as the subprocess route does.
    /// </summary>
    public static async Task<PyLabview.Run?> TryRunAsync(PyLabview.Bundle bundle, string script,
                                                         IReadOnlyList<string> args, int timeoutSeconds,
                                                         CancellationToken ct)
    {
        if (!Enabled || Locate() is not { } workerPy) return null;
        await Slots.WaitAsync(ct);
        try
        {
            // twice at most: a worker answering `stale` ran nothing, its replacement runs it
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var worker = Take(bundle, workerPy) ?? Start(bundle, workerPy);
                if (worker is null) return null;
                Worker.Answer? answer;
                try
                {
                    answer = await worker.CallAsync(script, args, bundle.Directory, timeoutSeconds, ct);
                }
                catch
                {
                    Retire(worker);
                    throw;
                }
                if (answer is null)
                {
                    Retire(worker);
                    return null;
                }
                if (answer.Stale)
                {
                    Retire(worker);
                    continue;
                }
                if (answer.Recycle || worker.Requests >= MaxRequests) Retire(worker);
                else Return(worker);
                return new PyLabview.Run(answer.Exit, answer.StdOut, answer.StdErr, answer.ElapsedMs);
            }
            return null;
        }
        finally
        {
            Slots.Release();
        }
    }

    /// <summary>
    /// Starts one worker and has it import the composer and pylabview, so the first generation
    /// after a start does not pay for it: measured 1.38 s for the first compose in a fresh worker
    /// against 0.6 s for the same one after. Fire-and-forget from the start-up; a station with no
    /// bundle simply has nothing to warm.
    /// </summary>
    public static async Task WarmAsync()
    {
        try
        {
            if (!Enabled || PyLabview.Locate() is not { } bundle || Locate() is not { } workerPy) return;
            await Slots.WaitAsync();
            try
            {
                var worker = Start(bundle, workerPy);
                if (worker is null) return;
                if (await worker.WarmAsync()) Return(worker);
                else Retire(worker);
            }
            finally
            {
                Slots.Release();
            }
        }
        catch (Exception)
        {
            // a warm-up that fails costs the first call its imports, nothing more
        }
    }

    /// <summary>worker.py beside the composer, or null.</summary>
    private static string? Locate()
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup is null) return null;
        var path = Path.Combine(setup.ComposerDirectory, WorkerScript);
        return File.Exists(path) ? path : null;
    }

    private static Worker? Take(PyLabview.Bundle bundle, string workerPy)
    {
        lock (Gate)
        {
            for (var i = Idle.Count - 1; i >= 0; i--)
            {
                var w = Idle[i];
                Idle.RemoveAt(i);
                if (w.Matches(bundle, workerPy) && w.Alive) return w;
                w.Dispose();
                All.Remove(w);
            }
        }
        return null;
    }

    private static Worker? Start(PyLabview.Bundle bundle, string workerPy)
    {
        if (Interlocked.Exchange(ref _hooked, 1) == 0)
            AppDomain.CurrentDomain.ProcessExit += (_, _) => StopAll();
        var w = Worker.Start(bundle, workerPy);
        if (w is null) return null;
        lock (Gate)
        {
            All.Add(w);
            Started++;
        }
        return w;
    }

    private static void Return(Worker w)
    {
        lock (Gate) Idle.Add(w);
    }

    private static void Retire(Worker w)
    {
        w.Dispose();
        lock (Gate) All.Remove(w);
    }

    /// <summary>Ends every worker. Each also ends by itself when the server's end of its stdin closes.</summary>
    public static void StopAll()
    {
        List<Worker> all;
        lock (Gate)
        {
            all = [.. All];
            All.Clear();
            Idle.Clear();
        }
        foreach (var w in all) w.Dispose();
    }

    private sealed class Worker : IDisposable
    {
        private readonly Process _process;
        private readonly string _python;
        private readonly string _script;
        private long _next;

        public int Requests { get; private set; }

        private Worker(Process process, string python, string script)
        {
            _process = process;
            _python = python;
            _script = script;
        }

        public bool Alive
        {
            get
            {
                try { return !_process.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
        }

        public bool Matches(PyLabview.Bundle bundle, string workerPy) =>
            string.Equals(_python, bundle.PythonExe, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_script, workerPy, StringComparison.OrdinalIgnoreCase);

        public static Worker? Start(PyLabview.Bundle bundle, string workerPy)
        {
            var info = new ProcessStartInfo(bundle.PythonExe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = bundle.Directory,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            // the same warning filter as the subprocess route (PyLabview.RunAsync says why)
            info.ArgumentList.Add("-W");
            info.ArgumentList.Add("ignore::SyntaxWarning");
            info.ArgumentList.Add(workerPy);
            try
            {
                var p = new Process { StartInfo = info };
                p.Start();
                // what a script writes past its captured streams goes to the worker's own
                // stderr; drained so it never blocks, and dropped
                p.ErrorDataReceived += (_, _) => { };
                p.BeginErrorReadLine();
                return new Worker(p, bundle.PythonExe, workerPy);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return null;
            }
        }

        public async Task<bool> WarmAsync()
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                var id = ++_next;
                await _process.StandardInput.WriteLineAsync(
                    new JsonObject { ["id"] = id, ["warm"] = true }.ToJsonString().AsMemory(), budget.Token);
                await _process.StandardInput.FlushAsync(budget.Token);
                var line = await _process.StandardOutput.ReadLineAsync(budget.Token);
                return line is not null && (JsonNode.Parse(line) as JsonObject)?["exit"]?.GetValue<int>() == 0;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonException)
            {
                return false;
            }
        }

        internal sealed record Answer(int Exit, string StdOut, string StdErr, long ElapsedMs, bool Recycle, bool Stale);

        /// <summary>The call's answer; null when the worker died or answered something unreadable.</summary>
        public async Task<Answer?> CallAsync(string script, IReadOnlyList<string> args, string cwd,
                                             int timeoutSeconds, CancellationToken ct)
        {
            Requests++;
            var id = ++_next;
            var request = new JsonObject
            {
                ["id"] = id,
                ["script"] = script,
                ["argv"] = new JsonArray([.. args.Select(a => (JsonNode?)JsonValue.Create(a))]),
                ["cwd"] = cwd,
            };
            var stopwatch = Stopwatch.StartNew();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600)));
            string? line;
            try
            {
                await _process.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), budget.Token);
                await _process.StandardInput.FlushAsync(budget.Token);
                line = await _process.StandardOutput.ReadLineAsync(budget.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(PyLabview.TimeoutMessage(timeoutSeconds));
            }
            catch (IOException)
            {
                return null;
            }
            stopwatch.Stop();
            if (line is null) return null;
            try
            {
                var node = JsonNode.Parse(line) as JsonObject;
                if (node is null || (long?)node["id"] != id) return null;
                if (node["stale"]?.GetValue<bool>() == true) return new Answer(0, "", "", 0, false, true);
                return new Answer(node["exit"]?.GetValue<int>() ?? 1,
                                  (string?)node["stdout"] ?? "", (string?)node["stderr"] ?? "",
                                  stopwatch.ElapsedMilliseconds,
                                  node["recycle"]?.GetValue<bool>() == true, false);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            try { _process.StandardInput.Close(); } catch (Exception) { /* already gone */ }
            try
            {
                if (!_process.WaitForExit(200)) _process.Kill(entireProcessTree: true);
            }
            catch (Exception) { /* already gone */ }
            _process.Dispose();
        }
    }
}
