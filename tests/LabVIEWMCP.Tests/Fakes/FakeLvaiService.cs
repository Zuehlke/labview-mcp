using Google.Protobuf;
using Grpc.Core;
using LabVIEWMcp.Lvai;

namespace LabVIEWMcp.Tests.Fakes;

/// <summary>
/// A scriptable stand-in for LabVIEW's lvai.LVAI service, implementing all 23 RPCs.
///
/// Tests run the real tools against this over a real HTTP/2 channel, so request mapping,
/// protobuf serialization, streaming semantics and deadlines are all genuinely exercised -
/// only LabVIEW itself is replaced.
/// </summary>
internal sealed class FakeLvaiService : LVAI.LVAIBase
{
    private readonly object _gate = new();
    private readonly List<(string Method, IMessage Message)> _received = [];

    // ---- failure injection ----
    public StatusCode? FailWith { get; set; }
    public string FailDetail { get; set; } = "injected failure";
    /// <summary>When > 0, only that many calls fail and the counter decrements.</summary>
    public int FailCount { get; set; } = -1;
    /// <summary>
    /// Restrict failures to one RPC. Needed whenever the connection probe
    /// (GetApplicationConfiguration) must still succeed - e.g. to reach a tool's error path,
    /// or to exercise the reconnect-and-retry logic.
    /// </summary>
    public string? FailOnMethod { get; set; }

    // ---- streaming behaviour ----
    public int StreamCount { get; set; } = 2;
    public int BidiPushCount { get; set; } = 1;
    public TimeSpan StreamDelay { get; set; } = TimeSpan.Zero;
    /// <summary>Keep the stream open after the pushes, so the client has to time out.</summary>
    public bool StreamForever { get; set; }

    // ---- canned payloads ----
    public string Language { get; set; } = "English";
    public string InfoJson { get; set; } = """{"viName":"Fake.vi","nodes":[]}""";
    public int ErrorCode { get; set; }
    public string ErrorMessage { get; set; } = "No Error";
    public Dictionary<string, string> Outputs { get; } = [];
    public List<string> GeneratedFiles { get; } = [];
    /// <summary>When set, ConvertVIToAIXML writes this to the requested aiXMLFilePath.</summary>
    public string? XmlFileContent { get; set; }
    /// <summary>When set, ConvertAIXMLToVI writes this to the requested viPath.</summary>
    public string? ViFileContent { get; set; }
    /// <summary>
    /// Per-RPC override of <see cref="ErrorCode"/>, honoured by ValidateAIXML,
    /// ConvertAIXMLToVI and RunVIAsTopLevel. A COMPOSED tool makes several calls in one
    /// invocation, so reaching its later failure paths needs the earlier calls to succeed -
    /// lvai_set_vi_icon validates before it generates, and a single global code cannot
    /// express "validation passes, generation fails".
    /// </summary>
    public Dictionary<string, int> ErrorCodeByMethod { get; } = [];

    private int CodeFor(string method) =>
        ErrorCodeByMethod.TryGetValue(method, out var code) ? code : ErrorCode;

    public IReadOnlyList<(string Method, IMessage Message)> Received
    {
        get { lock (_gate) return [.. _received]; }
    }

    public T Last<T>(string method) where T : IMessage
    {
        lock (_gate)
        {
            var hit = _received.LastOrDefault(r => r.Method == method);
            if (hit.Message is T typed) return typed;
            throw new InvalidOperationException(
                $"No {method} request recorded. Recorded: " +
                string.Join(", ", _received.Select(r => r.Method).Distinct()));
        }
    }

    public int CountOf(string method)
    {
        lock (_gate) return _received.Count(r => r.Method == method);
    }

    /// <summary>
    /// Wait until the server has recorded at least <paramref name="count"/> messages for a
    /// method. Inbound bidi messages are recorded on a background drain, so asserting
    /// immediately after the tool returns would be a race.
    /// </summary>
    public async Task WaitForAsync(string method, int count = 1, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (CountOf(method) >= count) return;
            await Task.Delay(20);
        }
        throw new TimeoutException(
            $"Expected {count} '{method}' message(s), saw {CountOf(method)}.");
    }

    private void Record(string method, IMessage message)
    {
        lock (_gate) _received.Add((method, message));
    }

    private void ThrowIfConfigured(string method)
    {
        if (FailWith is not { } code) return;
        if (FailOnMethod is not null && FailOnMethod != method) return;
        lock (_gate)
        {
            if (FailCount == 0) return;
            if (FailCount > 0) FailCount--;
        }
        throw new RpcException(new Status(code, FailDetail));
    }

    private Task<T> Unary<T>(string method, IMessage request, Func<T> make)
    {
        Record(method, request);
        ThrowIfConfigured(method);
        return Task.FromResult(make());
    }

    private async Task ServerStream<T>(
        string method, IMessage request, IServerStreamWriter<T> responses,
        Func<int, T> make, ServerCallContext context)
    {
        Record(method, request);
        ThrowIfConfigured(method);
        await PushAsync(StreamCount, responses, make, context);
    }

    private async Task Bidi<TRequest, TResponse>(
        string method, IAsyncStreamReader<TRequest> requests,
        IServerStreamWriter<TResponse> responses, Func<int, TResponse> make,
        ServerCallContext context)
        where TRequest : IMessage
    {
        // The opening of the bidi call itself; real payloads are recorded as "<method>:in".
        Record(method, new GetApplicationConfigurationRequest());
        ThrowIfConfigured(method);

        // Drain concurrently: the client's answer only arrives AFTER it has read a push.
        var drain = Task.Run(async () =>
        {
            try
            {
                while (await requests.MoveNext(context.CancellationToken))
                    Record(method + ":in", requests.Current);
            }
            catch (Exception e) when (e is OperationCanceledException or RpcException or IOException)
            {
                // Client hung up - expected on the timeout paths.
            }
        }, CancellationToken.None);

        await PushAsync(BidiPushCount, responses, make, context);
        try { await drain; } catch (OperationCanceledException) { }
    }

    private async Task PushAsync<T>(
        int count, IServerStreamWriter<T> responses, Func<int, T> make, ServerCallContext context)
    {
        try
        {
            for (var i = 0; i < count; i++)
            {
                if (StreamDelay > TimeSpan.Zero)
                    await Task.Delay(StreamDelay, context.CancellationToken);
                await responses.WriteAsync(make(i));
            }
            if (StreamForever)
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
        }
        catch (Exception e) when (e is OperationCanceledException or RpcException or IOException)
        {
            // The client stopped reading (limit reached or timeout). Not an error here.
        }
    }

    // ================= unary =================

    public override Task<GetApplicationConfigurationResponse> GetApplicationConfiguration(
        GetApplicationConfigurationRequest request, ServerCallContext context) =>
        Unary(nameof(GetApplicationConfiguration), request,
            () => new GetApplicationConfigurationResponse { Language = Language });

    public override Task<FilterPaletteSearchCandidatesResponse> FilterPaletteSearchCandidates(
        FilterPaletteSearchCandidatesRequest request, ServerCallContext context) =>
        Unary(nameof(FilterPaletteSearchCandidates), request, () =>
        {
            var response = new FilterPaletteSearchCandidatesResponse();
            foreach (var guid in request.ItemGuids)
                response.Items.Add(new PaletteFilterResult
                {
                    Id = guid,
                    Title = $"Title of {guid}",
                    Description = "desc",
                    Icon = "icon.ico",
                    PaletteHierarchy = "Programming>>File IO",
                    HelpPath = "help.html",
                });
            return response;
        });

    public override Task<FilterExampleSearchCandidatesResponse> FilterExampleSearchCandidates(
        FilterExampleSearchCandidatesRequest request, ServerCallContext context) =>
        Unary(nameof(FilterExampleSearchCandidates), request, () =>
        {
            var response = new FilterExampleSearchCandidatesResponse();
            foreach (var path in request.ExamplePaths)
                response.Examples.Add(new ExampleFilterResult { Path = path, Description = "example" });
            return response;
        });

    public override Task<LogUsageDataResponse> LogUsageData(
        LogUsageDataRequest request, ServerCallContext context) =>
        Unary(nameof(LogUsageData), request, () => new LogUsageDataResponse());

    public override Task<OpenFileResponse> OpenFile(
        OpenFileRequest request, ServerCallContext context) =>
        Unary(nameof(OpenFile), request,
            () => new OpenFileResponse { ErrorCode = ErrorCode, ErrorMessage = ErrorMessage });

    public override Task<FindPaletteItemResponse> FindPaletteItem(
        FindPaletteItemRequest request, ServerCallContext context) =>
        Unary(nameof(FindPaletteItem), request,
            () => new FindPaletteItemResponse { ErrorCode = ErrorCode, ErrorMessage = ErrorMessage });

    public override Task<DropPaletteItemResponse> DropPaletteItem(
        DropPaletteItemRequest request, ServerCallContext context) =>
        Unary(nameof(DropPaletteItem), request,
            () => new DropPaletteItemResponse { ErrorCode = ErrorCode, ErrorMessage = ErrorMessage });

    public override Task<ValidateAIXMLResponse> ValidateAIXML(
        ValidateAIXMLRequest request, ServerCallContext context) =>
        Unary(nameof(ValidateAIXML), request, () => new ValidateAIXMLResponse
        {
            ErrorCode = CodeFor(nameof(ValidateAIXML)),
            ErrorMessage = ErrorMessage,
        });

    public override Task<ApplyAIXMLToVIResponse> ApplyAIXMLToVI(
        ApplyAIXMLToVIRequest request, ServerCallContext context) =>
        Unary(nameof(ApplyAIXMLToVI), request,
            () => new ApplyAIXMLToVIResponse { ErrorCode = ErrorCode, ErrorMessage = ErrorMessage });

    public override Task<ConvertVIToAIXMLResponse> ConvertVIToAIXML(
        ConvertVIToAIXMLRequest request, ServerCallContext context) =>
        Unary(nameof(ConvertVIToAIXML), request, () =>
        {
            if (XmlFileContent is not null)
                File.WriteAllText(request.AiXMLFilePath, XmlFileContent);
            return new ConvertVIToAIXMLResponse { ErrorCode = ErrorCode, ErrorMessage = ErrorMessage };
        });

    public override Task<ConvertAIXMLToVIResponse> ConvertAIXMLToVI(
        ConvertAIXMLToVIRequest request, ServerCallContext context) =>
        Unary(nameof(ConvertAIXMLToVI), request, () =>
        {
            if (ViFileContent is not null)
                File.WriteAllText(request.ViPath, ViFileContent);
            return new ConvertAIXMLToVIResponse
            {
                ErrorCode = CodeFor(nameof(ConvertAIXMLToVI)),
                ErrorMessage = ErrorMessage,
            };
        });

    public override Task<RunVIAsTopLevelResponse> RunVIAsTopLevel(
        RunVIAsTopLevelRequest request, ServerCallContext context) =>
        Unary(nameof(RunVIAsTopLevel), request, () =>
        {
            var response = new RunVIAsTopLevelResponse
            {
                ErrorCode = CodeFor(nameof(RunVIAsTopLevel)),
                ErrorMessage = ErrorMessage,
            };
            foreach (var (key, value) in Outputs) response.Outputs[key] = value;
            return response;
        });

    public override Task<BuildFromBuildSpecificationResponse> BuildFromBuildSpecification(
        BuildFromBuildSpecificationRequest request, ServerCallContext context) =>
        Unary(nameof(BuildFromBuildSpecification), request, () =>
        {
            var response = new BuildFromBuildSpecificationResponse
            {
                ErrorCode = ErrorCode,
                ErrorMessage = ErrorMessage,
            };
            response.GeneratedFiles.AddRange(GeneratedFiles);
            return response;
        });

    // ================= server streaming =================

    public override Task GetDescribeVIPromptInfo(
        GetDescribeVIPromptInfoRequest request,
        IServerStreamWriter<GetDescribeVIPromptInfoResponse> responseStream,
        ServerCallContext context) =>
        ServerStream(nameof(GetDescribeVIPromptInfo), request, responseStream,
            i => new GetDescribeVIPromptInfoResponse { InfoJson = $"{InfoJson}#{i}" }, context);

    public override Task GetDescribeProjectPromptInfo(
        GetDescribeProjectPromptInfoRequest request,
        IServerStreamWriter<GetDescribeProjectPromptInfoResponse> responseStream,
        ServerCallContext context) =>
        ServerStream(nameof(GetDescribeProjectPromptInfo), request, responseStream,
            i => new GetDescribeProjectPromptInfoResponse { InfoJson = $"{InfoJson}#{i}" }, context);

    public override Task SearchInfoCache(
        SearchInfoCacheRequest request, IServerStreamWriter<InfoCacheResponse> responseStream,
        ServerCallContext context) =>
        ServerStream(nameof(SearchInfoCache), request, responseStream,
            i => new InfoCacheResponse
            {
                InfoJson = $"[{{\"hit\":{i}}}]",
                ErrorCode = ErrorCode,
                ErrorMessage = ErrorMessage,
            }, context);

    public override Task LookupInfoCacheItems(
        LookupInfoCacheItemsRequest request, IServerStreamWriter<InfoCacheResponse> responseStream,
        ServerCallContext context) =>
        ServerStream(nameof(LookupInfoCacheItems), request, responseStream,
            i => new InfoCacheResponse
            {
                InfoJson = $"[{{\"item\":{i}}}]",
                ErrorCode = ErrorCode,
                ErrorMessage = ErrorMessage,
            }, context);

    public override Task MonitorProjectChanges(
        MonitorProjectChangesRequest request,
        IServerStreamWriter<MonitorProjectChangesResponse> responseStream,
        ServerCallContext context) =>
        ServerStream(nameof(MonitorProjectChanges), request, responseStream,
            i => new MonitorProjectChangesResponse
            {
                ProjectPath = @"C:\p\Fake.lvproj",
                UpdateType = ProjectItemUpdateType.Modified,
                ItemName = $"Item{i}.vi",
                ItemPath = $@"C:\p\Item{i}.vi",
            }, context);

    // ================= bidirectional =================

    public override Task MonitorDiscussVI(
        IAsyncStreamReader<MonitorDiscussVIRequest> requestStream,
        IServerStreamWriter<MonitorDiscussVIResponse> responseStream, ServerCallContext context) =>
        Bidi(nameof(MonitorDiscussVI), requestStream, responseStream,
            _ => new MonitorDiscussVIResponse
            {
                ViPath = @"C:\p\Discussed.vi",
                ViName = "Discussed.vi",
                FileType = DiscussFileType.Vi,
            }, context);

    public override Task MonitorPaletteSearches(
        IAsyncStreamReader<MonitorPaletteSearchesRequest> requestStream,
        IServerStreamWriter<MonitorPaletteSearchesResponse> responseStream,
        ServerCallContext context) =>
        Bidi(nameof(MonitorPaletteSearches), requestStream, responseStream,
            i => new MonitorPaletteSearchesResponse
            {
                SearchString = $"write file {i}",
                Guid = "palette-guid-1",
            }, context);

    public override Task MonitorExampleSearches(
        IAsyncStreamReader<MonitorExampleSearchesRequest> requestStream,
        IServerStreamWriter<MonitorExampleSearchesResponse> responseStream,
        ServerCallContext context) =>
        Bidi(nameof(MonitorExampleSearches), requestStream, responseStream,
            i => new MonitorExampleSearchesResponse
            {
                SearchString = $"tcp example {i}",
                Guid = "example-guid-1",
            }, context);

    public override Task MonitorCodeCompletion(
        IAsyncStreamReader<MonitorCodeCompletionRequest> requestStream,
        IServerStreamWriter<MonitorCodeCompletionResponse> responseStream,
        ServerCallContext context) =>
        Bidi(nameof(MonitorCodeCompletion), requestStream, responseStream,
            _ => new MonitorCodeCompletionResponse
            {
                Guid = "cc-guid-1",
                Request = "add two numbers",
                Incomplete = false,
            }, context);

    public override Task MonitorFrontPanelCleanup(
        IAsyncStreamReader<MonitorFrontPanelCleanupRequest> requestStream,
        IServerStreamWriter<MonitorFrontPanelCleanupResponse> responseStream,
        ServerCallContext context) =>
        Bidi(nameof(MonitorFrontPanelCleanup), requestStream, responseStream,
            _ => new MonitorFrontPanelCleanupResponse
            {
                Guid = "fp-guid-1",
                Request = "{\"controls\":[]}",
                Incomplete = false,
            }, context);
}
