using System.Net;
using LabVIEWMcp.Grpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabVIEWMcp.Tests.Fakes;

/// <summary>
/// Hosts <see cref="FakeLvaiService"/> on a dynamic loopback port over plaintext HTTP/2 -
/// the same transport shape LabVIEW uses - and hands out an <see cref="LvaiConnection"/>
/// pinned to it. One instance per test keeps tests independent.
/// </summary>
internal sealed class LvaiTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<string> _tempFiles = [];

    private LvaiTestServer(WebApplication app, int port, FakeLvaiService service)
    {
        _app = app;
        Port = port;
        Service = service;
        Connection = new LvaiConnection(NullLogger<LvaiConnection>.Instance, port);
    }

    public int Port { get; }
    public FakeLvaiService Service { get; }
    public LvaiConnection Connection { get; }

    public static async Task<LvaiTestServer> StartAsync(bool withReflection = true)
    {
        var service = new FakeLvaiService();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.WebHost.ConfigureKestrel(options =>
            // Bind IPv4 loopback explicitly: ListenLocalhost(0) can pick DIFFERENT ports for
            // v4 and v6, and the client dials 127.0.0.1.
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));

        builder.Services.AddGrpc();
        builder.Services.AddSingleton(service);
        if (withReflection) builder.Services.AddGrpcReflection();

        var app = builder.Build();
        app.MapGrpcService<FakeLvaiService>();
        if (withReflection) app.MapGrpcReflectionService();

        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel reported no bound address.");
        var port = new Uri(addresses.First()).Port;

        return new LvaiTestServer(app, port, service);
    }

    /// <summary>A path in a per-test temp folder, deleted on dispose.</summary>
    public string TempPath(string fileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "lvaimcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        _tempFiles.Add(path);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();

        foreach (var file in _tempFiles)
        {
            try
            {
                var directory = Path.GetDirectoryName(file);
                if (directory is not null && Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Temp cleanup is best effort.
            }
        }
    }
}
