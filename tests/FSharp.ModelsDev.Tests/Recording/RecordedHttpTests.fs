namespace FSharp.ModelsDev.Tests

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.ModelsDev
open FSharp.ModelsDev.Tests.Recording

type private TemporaryCassette() =
    let path = Path.Combine(Path.GetTempPath(), "modelsdev-recording-" + Guid.NewGuid().ToString("N") + ".json")
    member _.Path = path
    interface IDisposable with member _.Dispose() = if File.Exists path then File.Delete path

type private RecordingStub(send: HttpRequestMessage -> CancellationToken -> Task<HttpResponseMessage>) =
    inherit HttpMessageHandler()
    override _.SendAsync(request, ct) = send request ct

module private RecordingTestData =
    let write (path: string) (bodies: string[]) =
        let cassette = Cassette()
        for body in bodies do
            let request = RecordedRequest(Method = "POST", Url = "https://example.test/chat", Body = body)
            request.Headers["content-type"] <- "application/json"
            let response = RecordedResponse(Body = "你好\n\n")
            response.Headers["content-type"] <- "text/event-stream"
            cassette.Interactions.Add(RecordedInteraction(Request = request, Response = response))
        File.WriteAllText(path, JsonSerializer.Serialize(cassette, CassetteJson.options))
    let headers () =
        let result = Dictionary<string, string>()
        result["Authorization"] <- "Bearer private-test-token"
        result :> IReadOnlyDictionary<string, string>

[<Trait("Category", "Recorded")>]
type RecordedHttpTests() =
    [<Fact>]
    member _.``replay matches canonical JSON and fragments UTF8 without network access``() = task {
        use file = new TemporaryCassette()
        RecordingTestData.write file.Path [| """{"a":1,"b":[1,2]}""" |]
        use handler = new RecordedHttpHandler(file.Path)
        use http = new HttpClient(handler)
        let transport = HttpChatTransport(http)
        let lines = transport.PostStream("https://example.test/chat", RecordingTestData.headers(), """{"b":[1,2],"a":1}""", CancellationToken.None)
        use iterator = lines.GetAsyncEnumerator()
        let result = ResizeArray<string>()
        let mutable more = true
        while more do
            let! next = iterator.MoveNextAsync().AsTask()
            more <- next
            if next then result.Add iterator.Current
        Assert.Equal<string>([| "你好"; "" |], result)
        handler.VerifyComplete()
    }

    [<Theory>]
    [<InlineData("method")>]
    [<InlineData("URL")>]
    [<InlineData("headers")>]
    [<InlineData("body")>]
    member _.``request mismatches fail without echoing request data``(changed: string) = task {
        use file = new TemporaryCassette()
        RecordingTestData.write file.Path [| """{"a":[1,2]}""" |]
        use handler = new RecordedHttpHandler(file.Path)
        use http = new HttpClient(handler)
        let method = if changed = "method" then HttpMethod.Get else HttpMethod.Post
        let url = if changed = "URL" then "https://example.test/other?code=private-test-token" else "https://example.test/chat"
        use request = new HttpRequestMessage(method, url)
        let body = if changed = "body" then """{"a":[2,1]}""" else """{"a":[1,2]}"""
        request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer private-test-token") |> ignore
        if changed = "headers" then request.Headers.TryAddWithoutValidation("anthropic-version", "different") |> ignore
        let! error = Assert.ThrowsAsync<InvalidOperationException>(fun () -> http.SendAsync(request) :> Task)
        Assert.Contains(changed, error.Message)
        Assert.DoesNotContain("private-test-token", error.Message)
        Assert.Throws<InvalidOperationException>(fun () -> handler.VerifyComplete()) |> ignore
    }

    [<Fact>]
    member _.``missing cassettes never initiate recording``() =
        use file = new TemporaryCassette()
        Assert.Throws<FileNotFoundException>(fun () -> new RecordedHttpHandler(file.Path) |> ignore) |> ignore
        Assert.False(File.Exists file.Path)

    [<Fact>]
    member _.``unread responses are failures even when every request matched``() = task {
        use file = new TemporaryCassette()
        RecordingTestData.write file.Path [| "{}" |]
        use handler = new RecordedHttpHandler(file.Path)
        use http = new HttpClient(handler)
        Assert.Throws<InvalidOperationException>(fun () -> handler.VerifyComplete()) |> ignore
        use request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/chat")
        request.Content <- new StringContent("{}", Encoding.UTF8, "application/json")
        use! response = http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
        use! stream = response.Content.ReadAsStreamAsync()
        let! _ = stream.ReadAsync(Memory<byte>(Array.zeroCreate 1))
        Assert.Throws<InvalidOperationException>(fun () -> handler.VerifyComplete()) |> ignore
    }

    [<Fact>]
    member _.``unconsumed interactions are failures even when a response was read``() = task {
        use file = new TemporaryCassette()
        RecordingTestData.write file.Path [| "{}"; "{}" |]
        use handler = new RecordedHttpHandler(file.Path)
        use http = new HttpClient(handler)
        let! _ = HttpChatTransport(http).Post("https://example.test/chat", RecordingTestData.headers(), "{}", CancellationToken.None)
        Assert.Throws<InvalidOperationException>(fun () -> handler.VerifyComplete()) |> ignore
    }

    [<Theory>]
    [<InlineData("missing-version")>]
    [<InlineData("unknown-version")>]
    [<InlineData("websocket")>]
    member _.``invalid versions and unsupported transports fail explicitly``(change: string) =
        use file = new TemporaryCassette()
        RecordingTestData.write file.Path [| "{}" |]
        let root = JsonNode.Parse(File.ReadAllText file.Path)
        match change with
        | "missing-version" -> root.AsObject().Remove("version") |> ignore
        | "unknown-version" -> root["version"] <- JsonValue.Create 2
        | _ -> root.["interactions"].[0].["transport"] <- JsonValue.Create "websocket"
        File.WriteAllText(file.Path, root.ToJsonString())
        Assert.Throws<InvalidOperationException>(fun () -> new RecordedHttpHandler(file.Path) |> ignore) |> ignore

    [<Fact>]
    member _.``recording is explicit atomic redacted and preserves HTTP errors``() = task {
        use file = new TemporaryCassette()
        let mutable calls = 0
        let source = new RecordingStub(fun _ _ -> task {
            calls <- calls + 1
            let response = new HttpResponseMessage(if calls = 1 then HttpStatusCode.Unauthorized else HttpStatusCode.OK)
            response.Content <- new StringContent("""{"access_token":"rotated-private-token","echo":"private-test-token"}""", Encoding.UTF8, "application/json")
            response.Headers.TryAddWithoutValidation("Set-Cookie", "session=private-cookie") |> ignore
            return response
        })
        use recorder = new RecordedHttpHandler(file.Path, Record, source, fun _ -> null)
        use http = new HttpClient(recorder)
        let transport = HttpChatTransport(http)
        let url = "https://example.test/chat?key=private-query-key"
        let headers = RecordingTestData.headers()
        let body = """{"client_secret":"private-client-secret","code":"private-code","input":"hello"}"""
        let! error = Assert.ThrowsAsync<AiException>(fun () -> transport.Post(url, headers, body, CancellationToken.None) :> Task)
        Assert.Equal(401, error.StatusCode.Value)
        let! original = transport.Post(url, headers, body, CancellationToken.None)
        Assert.Contains("rotated-private-token", original.Body)
        Assert.False(File.Exists file.Path)
        recorder.Save()
        let text = File.ReadAllText file.Path
        for secret in [| "private-test-token"; "private-query-key"; "private-client-secret"; "private-code"; "rotated-private-token"; "private-cookie" |] do
            Assert.DoesNotContain(secret, text)
        Assert.Equal(2, calls)
        use replay = new RecordedHttpHandler(file.Path)
        use replayHttp = new HttpClient(replay)
        let replayTransport = HttpChatTransport(replayHttp)
        let! repeated = Assert.ThrowsAsync<AiException>(fun () -> replayTransport.Post(url, headers, body, CancellationToken.None) :> Task)
        Assert.Equal(401, repeated.StatusCode.Value)
        let! result = replayTransport.Post(url, headers, body, CancellationToken.None)
        Assert.Contains("[REDACTED]", result.Body)
        replay.VerifyComplete()
    }

    [<Fact>]
    member _.``recording refuses CI and existing files``() =
        use file = new TemporaryCassette()
        use source = new RecordingStub(fun _ _ -> failwith "Network must not be called")
        Assert.Throws<InvalidOperationException>(fun () ->
            new RecordedHttpHandler(file.Path, Record, source, fun key -> if key = "CI" then "true" else null) |> ignore) |> ignore
        RecordingTestData.write file.Path [| "{}" |]
        Assert.Throws<InvalidOperationException>(fun () ->
            new RecordedHttpHandler(file.Path, Record, source, fun _ -> null) |> ignore) |> ignore

    [<Fact>]
    member _.``SSE JSON escaped echoes and short credentials are redacted without corrupting JSON``() =
        let redactor = CassetteRedactor()
        let headers = Dictionary<string, string>()
        headers["Authorization"] <- "Bearer sensitive-token-value"
        headers["Set-Cookie"] <- "session=private-cookie; Max-Age=0"
        redactor.Headers(headers) |> ignore
        let frames = "event: update\ndata: {\"text\":\"sensitive-token-value\",\"access_token\":\"new-sensitive-token\",\"count\":100}\n\n"
        let safe = redactor.Body(false, frames)
        Assert.DoesNotContain("sensitive-token", safe)
        Assert.Contains("\"count\":100", safe)
        Assert.Contains("event: update\n", safe)
        let short = redactor.Body(true, """{"client_secret":"x","echo":"x","max_tokens":100}""")
        use parsed = JsonDocument.Parse short
        Assert.Equal("[REDACTED]", parsed.RootElement.GetProperty("echo").GetString())
        Assert.Equal(100, parsed.RootElement.GetProperty("max_tokens").GetInt32())
        let encoded = redactor.Body(false, """{"text":"sensitive\u002dtoken-value"}""")
        Assert.DoesNotContain("token-value", encoded)

    [<Fact>]
    member _.``canceled recording cannot be persisted``() = task {
        use file = new TemporaryCassette()
        let started = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        let source = new RecordingStub(fun _ ct -> task {
            started.SetResult()
            do! Task.Delay(Timeout.Infinite, ct)
            return new HttpResponseMessage(HttpStatusCode.OK)
        })
        use recorder = new RecordedHttpHandler(file.Path, Record, source, fun _ -> null)
        use http = new HttpClient(recorder)
        let transport = HttpChatTransport(http)
        use cancellation = new CancellationTokenSource()
        let pending = transport.Post("https://example.test/chat", RecordingTestData.headers(), "{}", cancellation.Token)
        do! started.Task.WaitAsync(TimeSpan.FromSeconds 5.)
        cancellation.Cancel()
        let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () ->
            pending :> Task)
        Assert.Throws<InvalidOperationException>(fun () -> recorder.Save()) |> ignore
        Assert.False(File.Exists file.Path)
    }

    [<Theory>]
    [<InlineData("../outside")>]
    [<InlineData("/absolute")>]
    [<InlineData("C:\\outside")>]
    member _.``recording names cannot escape their directory``(name: string) =
        Assert.Throws<ArgumentException>(fun () -> RecordingFiles.fixture name |> ignore) |> ignore
