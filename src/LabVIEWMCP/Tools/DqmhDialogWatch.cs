using System.Text.Json.Nodes;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Watches for LabVIEW's "Save changes before closing?" dialog while a DQMH scripter runs, and
/// answers it with "Save - All".
///
/// WHY IT EXISTS. Delacor's `Convert Event.vi` modifies `Request Events--cluster.ctl` and
/// `Obtain Request Events.vi` and then lets them leave memory unsaved - measured twice on
/// 2026-10-06 - so LabVIEW raises this dialog in the middle of Delacor's own code. It is modal,
/// and a modal stops the gRPC service: one run waited 299 s for a person. No save made before or
/// after the scripter can prevent it, because the dialog is raised INSIDE it.
///
/// WHY "SAVE - ALL" IS THE RIGHT ANSWER - the user's instruction of 2026-10-06 was to watch for it
/// and decide. Every scripted DQMH tool refuses to start while anything in the project folder has
/// unsaved changes, so whatever the dialog lists is the scripter's own work - the changes the tool
/// would save afterwards anyway. Not saving them leaves the module half-edited on disk: measured,
/// the renamed event's typedef would keep its old name. The dialog's own "Cancel" is disabled
/// ("Programmatic close cannot be cancelled").
///
/// HOW. The dialog is drawn by LabVIEW and exposes NO UI Automation elements (measured: 0), so it
/// cannot be invoked through an accessibility API. It opens with the focus on "Save - All", so the
/// watch brings it to the front and presses SPACE - the same route the dialog-driven event tool
/// has used since 2026-09-01. One press per dialog: if the focus has moved (someone clicked the
/// checkbox), a second SPACE could toggle it back, so a dialog that does not close is reported and
/// left to a person.
/// </summary>
internal sealed class DqmhDialogWatch : IAsyncDisposable
{
    internal const string SaveChangesTitle = "Save changes before closing?";

    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly List<JsonObject> _events = [];
    private readonly HashSet<IntPtr> _answered = [];

    private DqmhDialogWatch() => _loop = Task.Run(() => LoopAsync(_stop.Token));

    internal static DqmhDialogWatch Start() => new();

    /// <summary>What the watch saw and did, in order.</summary>
    internal JsonArray Events()
    {
        lock (_events)
            return new JsonArray([.. _events.Select(e => (JsonNode)e.DeepClone())]);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var dialog = DqmhTools.Win32.FindWindow(SaveChangesTitle);
                if (dialog != IntPtr.Zero && _answered.Add(dialog)) Answer(dialog);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                Record(new JsonObject { ["error"] = failure.Message });
            }
            try { await Task.Delay(300, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Answer(IntPtr dialog)
    {
        var entry = new JsonObject
        {
            ["at"] = DateTime.Now.ToString("HH:mm:ss.fff"),
            ["dialog"] = SaveChangesTitle,
            ["decision"] = "Save - All",
            ["reason"] = "the listed items are this call's own scripted changes - the call " +
                         "refuses to start while anything in the project is unsaved",
        };
        if (!DqmhTools.Win32.DesktopIsInteractive())
        {
            entry["result"] = "notPressed: no window holds the foreground (locked desktop), so a " +
                              "keystroke cannot reach the dialog - a person has to answer it";
            Record(entry);
            return;
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            DqmhTools.Win32.Foreground(dialog);
            Thread.Sleep(250);
            if (!DqmhTools.Win32.IsForeground(dialog)) continue;

            DqmhTools.Win32.PressSpace();
            for (var wait = 0; wait < 10; wait++)
            {
                Thread.Sleep(200);
                if (DqmhTools.Win32.FindWindow(SaveChangesTitle) != dialog)
                {
                    entry["result"] = "pressed: the dialog closed";
                    entry["attempt"] = attempt;
                    Record(entry);
                    return;
                }
            }
            entry["result"] = "pressedButStillOpen: the focus may not have been on Save - All; " +
                              "left for a person rather than pressed again";
            Record(entry);
            return;
        }
        entry["result"] = "notPressed: the dialog would not come to the foreground";
        Record(entry);
    }

    private void Record(JsonObject entry)
    {
        lock (_events) _events.Add(entry);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _loop; } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
