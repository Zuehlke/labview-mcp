namespace LabVIEWMcp.Infra;

/// <summary>
/// Clearing LabVIEW's auto-save recovery store before an unattended start.
///
/// WHY. Leftover auto-save data makes LabVIEW raise a RECOVERY DIALOG when it next starts, and a
/// modal dialog stops the entire lvai gRPC service until a human dismisses it - the same failure
/// mode as NI's "Replace existing?" prompt. An unattended start is exactly the case where nobody is
/// there to click it, so the store is emptied first.
///
/// WHAT THIS IS NOT. It is not a fix for LabVIEW leaving a few seconds after start-up. That was
/// measured and ruled out: with this directory verified empty, validating an AIXML document naming
/// an uncatalogued VI Server class still terminated the process in eight seconds, with the same two
/// `OMAutoClasses` entries in the log and zero new archives written.
/// `docs/ni-bug-validateaixml-crash.md` has that A/B. The archives in here are written when LabVIEW
/// STARTS and finds leftovers from an abnormal end, so a pile of them counts past crashes rather
/// than causing the next one.
///
/// The path is resolved through the Documents KNOWN FOLDER rather than assembled from
/// %USERPROFILE%, because Documents is commonly redirected - OneDrive being the usual culprit - and
/// a hardcoded path would clear a directory nobody is using while LabVIEW keeps reading the real
/// one.
/// </summary>
internal static class AutoSaveRecovery
{
    /// <summary>Where LabVIEW keeps auto-saved recovery data for the current user.</summary>
    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "LabVIEW Data", "LVAutoSave");

    /// <param name="Directory">The directory acted on, resolved.</param>
    /// <param name="Existed">False when there was nothing there at all, which is the common case.</param>
    /// <param name="FilesDeleted">How many files went.</param>
    /// <param name="BytesDeleted">Their total size, so the answer says what was destroyed.</param>
    /// <param name="DirectoriesDeleted">Subdirectories removed - LabVIEW's `archives\` and any others.</param>
    /// <param name="Failures">One line per item that could not be deleted, rather than an exception.</param>
    public sealed record Result(
        string Directory, bool Existed, int FilesDeleted, long BytesDeleted,
        int DirectoriesDeleted, IReadOnlyList<string> Failures)
    {
        public string Describe() =>
            !Existed ? $"no auto-save store at '{Directory}'"
            : FilesDeleted == 0 && DirectoriesDeleted == 0 && Failures.Count == 0
                ? "auto-save store already empty"
            : $"cleared {FilesDeleted} auto-save file(s), {Size(BytesDeleted)}" +
              (DirectoriesDeleted > 0 ? $" and {DirectoriesDeleted} subdirectory(ies)" : "") +
              (Failures.Count > 0 ? $"; {Failures.Count} could not be deleted" : "");

        /// <summary>
        /// Integer kB reported "0 kB" for anything under a kilobyte, which reads like nothing was
        /// deleted next to a count that says otherwise. This line is the only visible evidence the
        /// clear happened, so it should not contradict itself.
        /// </summary>
        private static string Size(long bytes) =>
            bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB"
            : bytes >= 1024 ? $"{bytes / 1024.0:0.#} kB"
            : $"{bytes} bytes";
    }

    /// <summary>
    /// Empty the store completely - every file and every subdirectory - leaving only the store's own
    /// directory, so LabVIEW has somewhere to write to.
    ///
    /// A locked item is COLLECTED rather than thrown, because this runs on the way to starting
    /// LabVIEW and refusing to start over one undeletable recovery file would be worse than the
    /// dialog it is trying to avoid.
    ///
    /// SUBDIRECTORIES GO TOO. An earlier version kept them, on the reasoning that LabVIEW recreates
    /// `archives\` itself so removing it gains nothing. That is true of `archives\` and not of the
    /// general case - the store is the user's and nothing in it should survive a clear they asked
    /// for. Deleting the files but leaving a tree of empty folders is also the kind of half-measure
    /// that reads as done and is not.
    /// </summary>
    public static Result Clear(string? directory = null)
    {
        var target = directory ?? DefaultDirectory();
        if (!System.IO.Directory.Exists(target))
            return new Result(target, Existed: false, 0, 0, 0, []);

        var failures = new List<string>();
        var count = 0;
        long bytes = 0;

        foreach (var file in System.IO.Directory.EnumerateFiles(
                     target, "*", SearchOption.AllDirectories))
        {
            try
            {
                var size = new FileInfo(file).Length;
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                count++;
                bytes += size;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{Path.GetFileName(file)}: {failure.Message}");
            }
        }

        // Deepest first, so a parent is never removed while a child it owns still exists.
        var directories = 0;
        foreach (var sub in System.IO.Directory
                     .EnumerateDirectories(target, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            try
            {
                System.IO.Directory.Delete(sub, recursive: true);
                directories++;
            }
            catch (DirectoryNotFoundException)
            {
                // Already gone: a deeper entry was removed with its parent. Not a failure.
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{Path.GetFileName(sub)} (directory): {failure.Message}");
            }
        }

        return new Result(target, Existed: true, count, bytes, directories, failures);
    }
}
