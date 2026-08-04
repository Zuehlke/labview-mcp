using System.Net;
using System.Net.Sockets;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Tests.Fakes;
using Xunit;

namespace LabVIEWMcp.Tests.Grpc;

public class PortDiscoveryTests
{
    /// <summary>
    /// One test for all environment cases: the variable is process-global, so splitting this
    /// across parallel test methods would be flaky.
    /// </summary>
    [Fact]
    public void ExplicitPort_accepts_valid_ports_and_rejects_the_rest()
    {
        const string name = "LABVIEW_GRPC_PORT";
        var original = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, null);
            Assert.Null(PortDiscovery.ExplicitPort());

            Environment.SetEnvironmentVariable(name, "49379");
            Assert.Equal(49379, PortDiscovery.ExplicitPort());

            Environment.SetEnvironmentVariable(name, "1");
            Assert.Equal(1, PortDiscovery.ExplicitPort());

            Environment.SetEnvironmentVariable(name, "65535");
            Assert.Equal(65535, PortDiscovery.ExplicitPort());

            // Out of range and nonsense must not produce a bogus candidate.
            foreach (var bad in new[] { "0", "-1", "65536", "99999999", "abc", "", "  ", "1.5" })
            {
                Environment.SetEnvironmentVariable(name, bad);
                Assert.Null(PortDiscovery.ExplicitPort());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }

    [Fact]
    public void Candidates_are_distinct_and_within_the_valid_port_range()
    {
        var candidates = PortDiscovery.Candidates();

        Assert.Equal(candidates.Select(c => c.Port).Distinct().Count(), candidates.Count);
        Assert.All(candidates, c => Assert.InRange(c.Port, 1, 65535));
        Assert.All(candidates, c => Assert.False(string.IsNullOrWhiteSpace(c.Source)));
    }

    [Fact]
    public async Task Candidates_include_a_freshly_opened_loopback_listener()
    {
        // Proves the enumeration actually reflects live sockets rather than a stale snapshot.
        await using var server = await LvaiTestServer.StartAsync(withReflection: false);

        var ports = PortDiscovery.Candidates().Select(c => c.Port).ToHashSet();
        Assert.Contains(server.Port, ports);
    }

    [Fact]
    public void Candidates_do_not_include_a_port_nobody_listens_on()
    {
        var free = FindFreePort();
        Assert.DoesNotContain(free, PortDiscovery.Candidates().Select(c => c.Port));
    }

    /// <summary>Bind then release: the port is free immediately afterwards.</summary>
    internal static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
