using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Every served tool, wrapped so that a bad ARGUMENT is answerable. <see cref="ToolArguments"/> has
/// the measurement and the reasoning; this is the shell that applies it.
///
/// A wrapper rather than a change to 26 required parameters across 20 tools: the schema keeps its
/// `required` array - which is real information for the caller - and no tool signature moves.
/// `DelegatingMcpServerTool` is the SDK's own extension point for this ("recommended as a base type
/// when building tools that can be chained around an underlying McpServerTool"), and the binding
/// failure this class catches was measured to be thrown INSIDE the inner tool's InvokeAsync, which
/// is what makes it reachable from here at all.
/// </summary>
internal sealed class DiagnosingTool(McpServerTool inner) : DelegatingMcpServerTool(inner)
{
    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var name = ProtocolTool.Name;
        var schema = ProtocolTool.InputSchema;
        var (properties, required) = ToolArguments.Shape(schema);

        var supplied = request.Params?.Arguments;
        // Captured before any renaming, because what the CALLER wrote is the useful half of the
        // report: "you sent vi_path" is what lets the next call be right.
        var received = supplied is null ? [] : supplied.Keys.ToList();

        if (request.Params is { } parameters && supplied is { Count: > 0 })
        {
            var renames = ToolArguments.Renames(properties, received);
            if (renames.Count > 0)
            {
                // A fresh dictionary rather than an in-place edit: `Arguments` is an IDictionary,
                // but nothing promises the instance the transport built is writable.
                var folded = new Dictionary<string, JsonElement>(supplied, StringComparer.Ordinal);
                foreach (var (from, to) in renames)
                {
                    folded[to] = folded[from];
                    folded.Remove(from);
                }

                parameters.Arguments = folded;
                supplied = folded;
            }
        }

        var missing = ToolArguments.Missing(required, supplied?.Keys.ToList());
        if (missing.Count > 0)
            return Failure(ToolArguments.MissingArguments(name, schema, missing, received));

        try
        {
            return await base.InvokeAsync(request, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A wanted-a-string failure is worth ONE retry with the values as their own JSON text.
            // Several parameters take a JSON document or a number in a string - `inputsJson` is a
            // JSON object by its own description, `section` is a heading number - and a client that
            // sends what it was shown gets rejected by the binder. Retrying is the fold that makes
            // those reachable; it happens only after a failure, so a value a tool accepted is never
            // reshaped. See ToolArguments.WantsString for the measurement.
            // Only the parameters the schema declares as `string` are reshaped. Stringifying
            // everything broke any call that paired a JSON-document argument with a bool or a
            // number - see ToolArguments.Stringified for the measurement.
            if (ToolArguments.WantsString(e) &&
                request.Params is { } retryParams &&
                ToolArguments.Stringified(supplied, ToolArguments.StringTyped(schema)) is { } stringified)
            {
                retryParams.Arguments = stringified;
                try
                {
                    return await base.InvokeAsync(request, cancellationToken);
                }
                catch (Exception second) when (second is not OperationCanceledException)
                {
                    // Report the ORIGINAL failure: it names the type the binder wanted, which is
                    // the useful half. The retry only ever adds quotes, so its own message is a
                    // consequence of the first problem rather than a second one.
                    return Failure(ToolArguments.InvocationProblem(name, schema, received, e));
                }
            }

            return Failure(ToolArguments.InvocationProblem(name, schema, received, e));
        }
    }

    /// <summary>
    /// Reported as a tool error carrying the JSON, not as a thrown exception: measured, the content
    /// of an `isError` result reaches the client verbatim, whereas an exception is what gets masked.
    /// </summary>
    private static CallToolResult Failure(string json) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = json }],
    };
}

internal static class DiagnosingToolExtensions
{
    /// <summary>
    /// Wrap every tool registered so far. MUST be called AFTER the registration that adds them
    /// (<c>WithToolsFromAssembly</c>), because it rewrites the descriptors that are already there;
    /// a tool registered afterwards would not be wrapped. The test suite asserts that every served
    /// tool comes back wrapped, so a future SDK that registers tools differently fails there rather
    /// than degrading in silence back to "An error occurred invoking '...'".
    /// </summary>
    public static IMcpServerBuilder WithArgumentDiagnostics(this IMcpServerBuilder builder)
    {
        var services = builder.Services;
        for (var i = 0; i < services.Count; i++)
        {
            var existing = services[i];
            if (existing.ServiceType != typeof(McpServerTool) || existing.IsKeyedService) continue;

            services[i] = ServiceDescriptor.Describe(
                typeof(McpServerTool),
                provider => new DiagnosingTool(Inner(existing, provider)),
                existing.Lifetime);
        }

        return builder;
    }

    /// <summary>The tool the original registration would have produced, whichever shape it used.</summary>
    private static McpServerTool Inner(ServiceDescriptor descriptor, IServiceProvider provider) =>
        (McpServerTool)(descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(provider)
            ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!));
}
