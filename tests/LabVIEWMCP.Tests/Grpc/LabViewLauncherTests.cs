using LabVIEWMcp.Grpc;
using Xunit;

namespace LabVIEWMCP.Tests.Grpc;

/// <summary>
/// The launcher's whole point is that "the launch call returned" and "LabVIEW is running" are
/// different facts. Measured 2026-08-06: a LabVIEW started as a child of the MCP server process
/// appeared and was gone 528 ms later, while <c>Process.Start</c> had reported success — so the
/// tool announced a running IDE that did not exist, and every following call failed with a port
/// scan that listed no LabVIEW listener at all.
///
/// <see cref="LabViewLauncher.ConfirmAsync"/> takes its process lister as a parameter precisely so
/// that this decision can be pinned without starting an IDE. Windows are milliseconds here; the
/// production values live in the launcher.
/// </summary>
public class LabViewLauncherConfirmTests
{
    private static readonly TimeSpan Appear = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Survive = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(15);

    private static IReadOnlySet<int> Pids(params int[] pids) => new HashSet<int>(pids);

    [Fact]
    public async Task AProcessThatNeverAppearsIsNotConfirmed()
    {
        var result = await LabViewLauncher.ConfirmAsync(
            () => Pids(), Pids(), Appear, Survive, Poll);

        Assert.False(result.Appeared);
        Assert.False(result.Survived);
        Assert.Null(result.Pid);
    }

    [Fact]
    public async Task AProcessThatAppearsAndStaysIsConfirmed()
    {
        var result = await LabViewLauncher.ConfirmAsync(
            () => Pids(4242), Pids(), Appear, Survive, Poll);

        Assert.True(result.Appeared);
        Assert.True(result.Survived);
        Assert.Equal(4242, result.Pid);
    }

    [Fact]
    public async Task AProcessThatAppearsThenVanishesIsReportedAsNotSurviving()
    {
        // The measured bug, in miniature: the pid exists on the first look and not on the second.
        var looks = 0;
        var result = await LabViewLauncher.ConfirmAsync(
            () => ++looks <= 1 ? Pids(4242) : Pids(), Pids(), Appear, Survive, Poll);

        Assert.True(result.Appeared);          // it really did start ...
        Assert.False(result.Survived);         // ... and that is not the same as running
        Assert.Equal(4242, result.Pid);
    }

    [Fact]
    public async Task AlreadyRunningProcessesDoNotCountAsAppearing()
    {
        // Otherwise a launch that silently did nothing would be confirmed by the instance that was
        // already there - the exact false positive that makes an intermittent bug look fixed.
        var result = await LabViewLauncher.ConfirmAsync(
            () => Pids(111), Pids(111), Appear, Survive, Poll);

        Assert.False(result.Appeared);
        Assert.Null(result.Pid);
    }

    [Fact]
    public async Task AProcessThatAppearsLateIsStillCaught()
    {
        var looks = 0;
        var result = await LabViewLauncher.ConfirmAsync(
            () => ++looks < 4 ? Pids() : Pids(777), Pids(), Appear, Survive, Poll);

        Assert.True(result.Appeared);
        Assert.Equal(777, result.Pid);
    }

    [Fact]
    public async Task ConfirmationHonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LabViewLauncher.ConfirmAsync(
                () => Pids(), Pids(), Appear, Survive, Poll, cts.Token));
    }

    [Fact]
    public async Task TheAppearWindowIsRespectedRatherThanWaitedOutForever()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await LabViewLauncher.ConfirmAsync(() => Pids(), Pids(), Appear, Survive, Poll);

        // Generous upper bound: the point is that it returns, not that it is millisecond-accurate.
        Assert.True(clock.Elapsed < Appear + TimeSpan.FromSeconds(2),
            $"gave up after {clock.Elapsed.TotalMilliseconds:0} ms, window was {Appear.TotalMilliseconds:0} ms");
    }
}
