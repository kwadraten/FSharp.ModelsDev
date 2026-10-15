namespace FSharp.ModelsDev.Tests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog
open FSharp.ModelsDev.Providers

type private CountingCredentials() =
    let mutable count = 0
    member _.Count = count
    interface ICredentialProvider with
        member _.GetHeaders(_, ct) =
            ct.ThrowIfCancellationRequested()
            count <- count + 1
            let headers = Dictionary<string, string>()
            headers.["Authorization"] <- "Bearer test-token"
            Task.FromResult(headers :> IReadOnlyDictionary<string, string>)

type private RecordingBackendTransport(body: string) =
    let mutable url = ""
    let mutable lastRequestBody = ""
    let responses = Queue<TransportResponse>()
    let mutable calls = 0
    member _.Url = url
    member _.RequestBody = lastRequestBody
    member _.Calls = calls
    member _.Responses = responses
    interface IChatTransport with
        member _.Post(address, _, requestBody, ct) =
            ct.ThrowIfCancellationRequested()
            url <- address
            lastRequestBody <- requestBody
            calls <- calls + 1
            Task.FromResult(if responses.Count > 0 then responses.Dequeue() else TransportResponse(200, body))
        member _.PostStream(address, _, _, _) =
            url <- address
            SeqAsyncEnumerable<string>([||]) :> IAsyncEnumerable<string>

type private RetryCredentials() =
    let mutable invalidations = 0
    member _.Invalidations = invalidations
    interface ICredentialProvider with
        member _.GetHeaders(_, _) =
            let headers = Dictionary<string, string>()
            headers.["Authorization"] <- "Bearer test-token"
            Task.FromResult(headers :> IReadOnlyDictionary<string, string>)
    interface IGoogleOAuthSessionProvider with
        member _.Invalidate(_) = invalidations <- invalidations + 1
        member _.GetSessionAsync(account, _) =
            Task.FromResult(GoogleOAuthSession(account, "test-token", "refresh", DateTimeOffset.UtcNow.AddHours(1.), "project"))

type private UnauthorizedStreamTransport(afterText: bool) =
    let mutable calls = 0
    member _.Calls = calls
    interface IChatTransport with
        member _.Post(_, _, _, _) = failwith "Only streaming is expected."
        member _.PostStream(_, _, _, _) =
            calls <- calls + 1
            let shouldFail = calls = 1
            let lines =
                [| "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":null}]}"
                   ""
                   "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}"
                   ""
                   "data: [DONE]"
                   "" |]
            { new IAsyncEnumerable<string> with
                member _.GetAsyncEnumerator(ct) =
                    let mutable index = -1
                    { new IAsyncEnumerator<string> with
                        member _.Current = lines.[index]
                        member _.MoveNextAsync() =
                            ct.ThrowIfCancellationRequested()
                            index <- index + 1
                            if shouldFail && index = (if afterText then 2 else 0) then
                                ValueTask<bool>(Task.FromException<bool>(AiException("expired", 401)))
                            else ValueTask<bool>(index < lines.Length)
                        member _.DisposeAsync() = ValueTask() } }

type ProviderBackendTests() =
    let openai = """{"id":"r","model":"test-model","choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}"""
    let gemini = """{"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":2,"totalTokenCount":3}}"""
    let anthropic = """{"id":"r","model":"test-model","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":2}}"""
    let responses = """{"id":"r","model":"test-model","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}],"usage":{"input_tokens":1,"output_tokens":2,"total_tokens":3}}"""

    [<Fact>]
    member _.``factory routes all backends and gets credentials at request time`` () : Task = task {
        let cases =
            [| "openai", null, "https://api.openai.com/v1/chat/completions", openai
               "openai", "openai-responses", "https://api.openai.com/v1/responses", responses
               "google", null, "https://generativelanguage.googleapis.com/v1beta/models/test-model:generateContent", gemini
               "google-vertex", null, "https://us-central1-aiplatform.googleapis.com/v1/projects/project/locations/us-central1/publishers/google/models/test-model:generateContent", gemini
               "anthropic", null, "https://api.anthropic.com/v1/messages", anthropic
               "google-vertex-anthropic", null, "https://us-central1-aiplatform.googleapis.com/v1/projects/project/locations/us-central1/publishers/anthropic/models/test-model:rawPredict", anthropic
               "antigravity", null, "https://cloudcode-pa.googleapis.com/v1internal:generateContent", "{\"response\":" + gemini + "}" |]
        for provider, protocol, expectedUrl, body in cases do
            let credentials = CountingCredentials()
            let transport = RecordingBackendTransport(body)
            let factory = ProviderFactory(SnapshotCatalog(), credentials, null, transport)
            let options = ProviderOptions(ProtocolId = protocol, ProjectId = "project", Location = "us-central1")
            let client = factory.CreateClient(provider, "account", "test-model", options)
            Assert.Equal(0, credentials.Count)
            let! result = client.Complete(ChatRequest("test-model", [| ChatMessage.User "hi" |]), CancellationToken.None)
            Assert.Equal("ok", result.Message.Text)
            Assert.Equal(expectedUrl, transport.Url)
            Assert.Equal(1, credentials.Count)
            Assert.Equal(3L, result.Usage.TotalTokens)
    }

    [<Fact>]
    member _.``explicit options are snapshotted when client is created`` () : Task = task {
        let transport = RecordingBackendTransport(responses)
        let factory = ProviderFactory(SnapshotCatalog(), CountingCredentials(), null, transport)
        let options = ProviderOptions(ProtocolId = "openai-responses", BaseUrl = "https://example.test/v1/")
        let client = factory.CreateClient("openai", "account", "test-model", options)
        options.BaseUrl <- "https://changed.test"
        let! _ = client.Complete(ChatRequest("test-model", [| ChatMessage.User "hi" |]), CancellationToken.None)
        Assert.Equal("https://example.test/v1/responses", transport.Url)
    }

    [<Fact>]
    member _.``OAuth unauthorized completion refreshes and retries once`` () : Task = task {
        let credentials = RetryCredentials()
        let transport = RecordingBackendTransport(openai)
        transport.Responses.Enqueue(TransportResponse(401, "expired"))
        let factory = ProviderFactory(SnapshotCatalog(), credentials, null, transport)
        let client = factory.CreateClient("openai", "account", "test-model")
        let! result = client.Complete(ChatRequest("test-model", [| ChatMessage.User "hi" |]), CancellationToken.None)
        Assert.Equal("ok", result.Message.Text)
        Assert.Equal(2, transport.Calls)
        Assert.Equal(1, credentials.Invalidations)
    }

    [<Fact>]
    member _.``OAuth second unauthorized response is not retried`` () : Task = task {
        let credentials = RetryCredentials()
        let transport = RecordingBackendTransport(openai)
        transport.Responses.Enqueue(TransportResponse(401, "expired"))
        transport.Responses.Enqueue(TransportResponse(401, "still expired"))
        let factory = ProviderFactory(SnapshotCatalog(), credentials, null, transport)
        let client = factory.CreateClient("openai", "account", "test-model")
        let! error = Assert.ThrowsAsync<AiException>(Func<Task>(fun () -> client.Complete(ChatRequest("test-model", [||]), CancellationToken.None)))
        Assert.Equal(401, error.StatusCode.Value)
        Assert.Equal(2, transport.Calls)
        Assert.Equal(1, credentials.Invalidations)
    }

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``OAuth stream retries only before any event reaches the caller`` (afterText: bool) : Task = task {
        let credentials = RetryCredentials()
        let transport = UnauthorizedStreamTransport(afterText)
        let client = ProviderFactory(SnapshotCatalog(), credentials, null, transport).CreateClient("openai", "account", "test-model")
        let collect () = task {
            let output = Text.StringBuilder()
            use iterator = client.Stream(ChatRequest("test-model", [| ChatMessage.User "hi" |]), CancellationToken.None).GetAsyncEnumerator(CancellationToken.None)
            let mutable more = true
            while more do
                let! moved = iterator.MoveNextAsync().AsTask()
                more <- moved
                if moved then
                    match iterator.Current with
                    | :? TextDelta as value -> output.Append(value.Text) |> ignore
                    | _ -> ()
            return output.ToString()
        }
        if afterText then
            let! error = Assert.ThrowsAsync<AiException>(Func<Task>(fun () -> collect()))
            Assert.Equal(401, error.StatusCode.Value)
            Assert.Equal(1, transport.Calls)
            Assert.Equal(0, credentials.Invalidations)
        else
            let! text = collect()
            Assert.Equal("ok", text)
            Assert.Equal(2, transport.Calls)
            Assert.Equal(1, credentials.Invalidations)
    }

    [<Fact>]
    member _.``request model overrides keep their own provider state through header configuration`` () : Task = task {
        let cases =
            [| "google", null, "gemini-original", "gemini-requested", gemini, "model"
               "anthropic", null, "claude-original", "claude-requested", anthropic, "model"
               "openai", "openai-responses", "openai-original", "openai-requested", responses.Replace("\"output\":[", "\"output\":[{\"type\":\"reasoning\",\"id\":\"r1\",\"encrypted_content\":\"opaque\"},"), "model" |]
        for provider, protocol, original, requested, body, stateModelKey in cases do
            let transport = RecordingBackendTransport(body)
            let headers = Dictionary<string, string>()
            headers.["x-test"] <- "snapshot"
            let options = ProviderOptions(ProtocolId = protocol, Headers = headers)
            let client = ProviderFactory(SnapshotCatalog(), CountingCredentials(), null, transport).CreateClient(provider, "account", original, options)
            let! result = client.Complete(ChatRequest(requested, [| ChatMessage.User "hi" |]), CancellationToken.None)
            use state = System.Text.Json.JsonDocument.Parse(result.Message.ProviderStateJson)
            Assert.Equal(requested, state.RootElement.GetProperty(stateModelKey).GetString())
    }

    [<Fact>]
    member _.``Vertex protocol inference distinguishes publishers`` () =
        let catalog = SnapshotCatalog() :> IModelCatalog
        for providerId, protocol in [ "google-vertex", "vertex-gemini"; "google-vertex-anthropic", "vertex-anthropic" ] do
            let mutable definition = Unchecked.defaultof<ProviderDefinition>
            Assert.True(catalog.TryGetProvider(providerId, &definition))
            Assert.Equal(protocol, definition.ProtocolId)

    [<Fact>]
    member _.``Claude implicit max tokens respects a smaller catalog output limit without changing the request`` () : Task = task {
        let baseCatalog = SnapshotCatalog() :> IModelCatalog
        let catalog =
            { new IModelCatalog with
                member _.SnapshotVersion = baseCatalog.SnapshotVersion
                member _.ProviderIds = baseCatalog.ProviderIds
                member _.TryGetProvider(providerId, result) = baseCatalog.TryGetProvider(providerId, &result)
                member _.TryGet(_, modelId, result) =
                    result <- ModelInfo("anthropic", modelId, modelId, modelId, ModelCapabilities.TextInput, Nullable(), Nullable 512L, [||])
                    true
                member _.GetModels(providerId) = baseCatalog.GetModels(providerId) }
        let transport = RecordingBackendTransport(anthropic)
        let client = ProviderFactory(catalog, CountingCredentials(), null, transport).CreateClient("anthropic", "account", "tiny")
        let request = ChatRequest("tiny", [| ChatMessage.User "hi" |])
        let! _ = client.Complete(request, CancellationToken.None)
        use body = System.Text.Json.JsonDocument.Parse(transport.RequestBody)
        Assert.Equal(512, body.RootElement.GetProperty("max_tokens").GetInt32())
        Assert.False(request.MaxTokens.HasValue)
    }

    [<Fact>]
    member _.``environment credentials use native API key headers`` () : Task = task {
        for providerId, protocol, expectedHeader in [ "google", "gemini", "x-goog-api-key"; "anthropic", "anthropic-messages", "x-api-key" ] do
            let variable = "MODELSDEV_TEST_" + Guid.NewGuid().ToString("N")
            Environment.SetEnvironmentVariable(variable, "test-key")
            try
                let definition = ProviderDefinition(providerId, providerId, null, protocol, [| variable |], null)
                let credentials = EnvCredentialProvider(definition) :> ICredentialProvider
                let! headers = credentials.GetHeaders("account", CancellationToken.None)
                Assert.Equal("test-key", headers.[expectedHeader])
                Assert.False(headers.ContainsKey("Authorization"))
            finally
                Environment.SetEnvironmentVariable(variable, null)
    }

    [<Fact>]
    member _.``Vertex environment configuration is never used as an API key`` () : Task = task {
        let definition = ProviderDefinition("google-vertex", "Vertex", null, "vertex-gemini", [| "GOOGLE_VERTEX_PROJECT" |], null)
        let credentials = EnvCredentialProvider(definition) :> ICredentialProvider
        let! error = Assert.ThrowsAsync<AiException>(Func<Task>(fun () -> task { let! _ = credentials.GetHeaders("account", CancellationToken.None) in return () }))
        Assert.Contains("Bearer", error.Message)
    }

    [<Fact>]
    member _.``OpenAI parser preserves same-frame text tool usage and terminal events`` () =
        let backend = OpenAIChatBackend("test-model", "account", CountingCredentials(), ProviderOptions()) :> IChatBackend
        let parser = backend.CreateStreamParser()
        let events = ResizeArray<ChatStreamEvent>()
        let payload = """data: {"choices":[{"index":0,"delta":{"content":"ok","tool_calls":[{"index":0,"id":"call","function":{"name":"tool","arguments":"{}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}"""
        events.AddRange(parser.PushLine(payload))
        events.AddRange(parser.PushLine(""))
        events.AddRange(parser.PushLine("data: [DONE]"))
        events.AddRange(parser.Finish())
        Assert.Single(events |> Seq.choose (function :? TextDelta as e -> Some e | _ -> None)) |> ignore
        Assert.Single(events |> Seq.choose (function :? ToolCallDelta as e -> Some e | _ -> None)) |> ignore
        let completed = Assert.Single(events |> Seq.choose (function :? Completed as e -> Some e | _ -> None))
        Assert.Equal(FinishReason.ToolCalls, completed.FinishReason)
        Assert.Equal(3L, completed.Usage.TotalTokens)

    [<Fact>]
    member _.``OpenAI parser rejects a truncated stream`` () =
        let backend = OpenAIChatBackend("test-model", "account", CountingCredentials(), ProviderOptions()) :> IChatBackend
        let parser = backend.CreateStreamParser()
        parser.PushLine("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}") |> ignore
        Assert.Throws<AiException>(fun () -> parser.Finish() |> ignore) |> ignore
