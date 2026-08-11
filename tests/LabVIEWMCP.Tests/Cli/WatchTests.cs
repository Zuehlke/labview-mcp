using LabVIEWMcp.Cli;
using Xunit;

namespace LabVIEWMCP.Tests.Cli;

/// <summary>
/// Only the argument gate is unit-testable here - the watch itself needs a live LabVIEW.
/// The gate matters: it must reject before touching the network, otherwise a typo costs the
/// user a five-minute wait that was never going to produce anything.
/// </summary>
public class WatchTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("codecompletion")]      // missing hyphen
    [InlineData("code_completion")]     // wrong separator
    [InlineData("Code-Completion")]     // wrong case
    [InlineData("monitor-everything")]
    public async Task UnknownMonitorNameIsRejectedWithoutConnecting(string? monitor)
    {
        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        try
        {
            // Port 1 is deliberately dead: if this ever tries to connect, the test hangs or
            // throws instead of returning the argument-error code.
            var exit = await Watch.RunAsync(port: 1, monitor: monitor, timeoutSeconds: 300);
            Assert.Equal(2, exit);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Contains("--watch needs one of", stderr.ToString());
    }

    [Fact]
    public async Task RejectionMessageListsEverySupportedMonitor()
    {
        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        try
        {
            await Watch.RunAsync(port: 1, monitor: "nope", timeoutSeconds: 300);
        }
        finally
        {
            Console.SetError(original);
        }

        var message = stderr.ToString();
        foreach (var name in new[]
                 {
                     "project-changes", "code-completion", "discuss-vi",
                     "palette-search", "example-search", "front-panel-cleanup",
                 })
            Assert.Contains(name, message);
    }
}
