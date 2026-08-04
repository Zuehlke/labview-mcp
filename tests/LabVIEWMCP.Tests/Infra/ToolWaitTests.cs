using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMCP.Tests.Infra;

/// <summary>
/// The clamp exists because of a measured failure, not a hunch: a 120 s monitor wait was
/// killed by the MCP client with "MCP error -32001: Request timed out", while 45 s returned
/// cleanly. These tests pin the boundary so nobody widens it back and reintroduces tools
/// that outlive the client's patience.
/// </summary>
public class ToolWaitTests
{
    [Fact]
    public void MaxToolWaitStaysUnderTheObservedClientLimit()
    {
        // The client tolerated 45 s and aborted 120 s. Anything at or above a minute is a
        // regression waiting to happen.
        Assert.InRange(Rpc.MaxToolWaitSeconds, 1, 59);
    }

    [Theory]
    [InlineData(60, 45)]
    [InlineData(120, 45)]
    [InlineData(3600, 45)]
    [InlineData(int.MaxValue, 45)]
    public void OverlongWaitsAreClampedDown(int requested, int expected) =>
        Assert.Equal(expected, Rpc.ClampToolWait(requested));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(int.MinValue, 1)]
    public void NonPositiveWaitsBecomeOneSecond(int requested, int expected) =>
        Assert.Equal(expected, Rpc.ClampToolWait(requested));

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(45)]
    public void WaitsWithinTheLimitPassThroughUnchanged(int requested) =>
        Assert.Equal(requested, Rpc.ClampToolWait(requested));

    [Fact]
    public void DeadlineStillAllowsLongValuesForNonMonitorCalls()
    {
        // A build can legitimately take many minutes; only the MONITOR waits are capped.
        var deadline = Rpc.Deadline(900);
        Assert.True(deadline > DateTime.UtcNow.AddSeconds(600),
            "Deadline must not be clamped to the tool-wait limit.");
    }
}
