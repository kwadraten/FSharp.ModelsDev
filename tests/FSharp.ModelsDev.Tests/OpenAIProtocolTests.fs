namespace FSharp.ModelsDev.Tests

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.ModelsDev
open FSharp.ModelsDev.Providers

/// Minimal IAsyncEnumerable over a fixed array, for stub transports and models.
type SeqAsyncEnumerable<'T>(items: 'T[]) =
    interface IAsyncEnumerable<'T> with
        member _.GetAsyncEnumerator(_ct: CancellationToken) : IAsyncEnumerator<'T> =
            let mutable index = -1

            { new IAsyncEnumerator<'T> with
                member _.Current = items[index]

                member _.MoveNextAsync() : ValueTask<bool> =
                    index <- index + 1
                    ValueTask<bool>(index < items.Length)

              interface IAsyncDisposable with
                  member _.DisposeAsync() = ValueTask() }

/// IChatTransport stub returning a preset response and preset SSE lines.
type FakeTransport(response: TransportResponse, streamLines: string[]) =
    interface IChatTransport with
        member _.Post(_url, _headers, _body, _ct) : Task<TransportResponse> =
            Task.FromResult(response)

        member _.PostStream(_url, _headers, _body, _ct) : IAsyncEnumerable<string> =
            SeqAsyncEnumerable<string>(streamLines) :> IAsyncEnumerable<string>

type OpenAIProtocolTests() =

    let protocol = OpenAICompatibleChatProtocol()

    [<Fact>]
    member _.``serialize includes model and message content`` () =
        let request = ChatRequest("gpt-5", [| ChatMessage.User "hi" |])
        use document = JsonDocument.Parse(protocol.SerializeRequest(request, false))
        let root = document.RootElement

        Assert.Equal("gpt-5", root.GetProperty("model").GetString())

        let messages = root.GetProperty("messages")
        Assert.Equal(1, messages.GetArrayLength())
        Assert.Equal("user", messages.[0].GetProperty("role").GetString())
        Assert.Equal("hi", messages.[0].GetProperty("content").GetString())

    [<Fact>]
    member _.``serialize maps system role`` () =
        let request =
            ChatRequest("m", [| ChatMessage.System "be nice"; ChatMessage.User "hi" |])

        use document = JsonDocument.Parse(protocol.SerializeRequest(request, false))
        let messages = document.RootElement.GetProperty("messages")

        Assert.Equal("system", messages.[0].GetProperty("role").GetString())
        Assert.Equal("be nice", messages.[0].GetProperty("content").GetString())
        Assert.Equal("user", messages.[1].GetProperty("role").GetString())
        Assert.Equal("hi", messages.[1].GetProperty("content").GetString())

    [<Fact>]
    member _.``serialize omits unset options`` () =
        let request = ChatRequest("m", [| ChatMessage.User "hi" |])
        use document = JsonDocument.Parse(protocol.SerializeRequest(request, false))
        let root = document.RootElement
        let mutable ignored = Unchecked.defaultof<JsonElement>

        Assert.False(root.TryGetProperty("temperature", &ignored))
        Assert.False(root.TryGetProperty("max_tokens", &ignored))
        Assert.False(root.TryGetProperty("top_p", &ignored))
        Assert.False(root.TryGetProperty("stop", &ignored))
        Assert.False(root.TryGetProperty("tools", &ignored))
        Assert.False(root.TryGetProperty("stream", &ignored))
        Assert.False(root.TryGetProperty("reasoning_effort", &ignored))

    [<Fact>]
    member _.``serialize includes temperature only when set`` () =
        let request = ChatRequest("m", [| ChatMessage.User "hi" |])
        request.Temperature <- Nullable 0.5

        use document = JsonDocument.Parse(protocol.SerializeRequest(request, false))
        Assert.Equal(0.5, document.RootElement.GetProperty("temperature").GetDouble())

    [<Fact>]
    member _.``serialize stream shape enables usage streaming`` () =
        let request = ChatRequest("m", [| ChatMessage.User "hi" |])
        use document = JsonDocument.Parse(protocol.SerializeRequest(request, true))
        let root = document.RootElement

        Assert.True(root.GetProperty("stream").GetBoolean())
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean())

    [<Fact>]
    member _.``serialize embeds tool parameters as raw json`` () =
        let request = ChatRequest("m", [| ChatMessage.User "hi" |])

        request.Tools <-
            [| ToolDefinition(
                   "get_weather",
                   "Get the weather",
                   """{"type":"object","properties":{"city":{"type":"string"}}}"""
               ) |]

        use document = JsonDocument.Parse(protocol.SerializeRequest(request, false))
        let tool = document.RootElement.GetProperty("tools").[0]

        Assert.Equal("function", tool.GetProperty("type").GetString())

        let function0 = tool.GetProperty("function")
        Assert.Equal("get_weather", function0.GetProperty("name").GetString())
        Assert.Equal("Get the weather", function0.GetProperty("description").GetString())

        let parameters = function0.GetProperty("parameters")
        Assert.Equal(JsonValueKind.Object, parameters.ValueKind)
        Assert.Equal("object", parameters.GetProperty("type").GetString())
        Assert.Equal("string", parameters.GetProperty("properties").GetProperty("city").GetProperty("type").GetString())

    [<Fact>]
    member _.``serialize writes reasoning effort`` () =
        let request = ChatRequest("m", [| ChatMessage.User "hi" |])
        request.ReasoningEffort <- "low"

        use document = JsonDocument.Parse(protocol.SerializeRequest(request, false))
        Assert.Equal("low", document.RootElement.GetProperty("reasoning_effort").GetString())

    [<Fact>]
    member _.``parse response maps all fields`` () =
        let body =
            """{"id":"chatcmpl-123","model":"gpt-5","choices":[{"index":0,"message":{"role":"assistant","content":"Hello!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":11,"completion_tokens":7,"total_tokens":18}}"""

        let response = protocol.ParseResponse(body)

        Assert.Equal("chatcmpl-123", response.Id)
        Assert.Equal("gpt-5", response.Model)
        Assert.Equal(Role.Assistant, response.Message.Role)
        Assert.Equal("Hello!", response.Message.Text)
        Assert.Equal(FinishReason.Stop, response.FinishReason)
        Assert.Equal(11L, response.Usage.InputTokens)
        Assert.Equal(7L, response.Usage.OutputTokens)
        Assert.Equal(18L, response.Usage.TotalTokens)

    [<Fact>]
    member _.``parse maps length finish reason`` () =
        let body =
            """{"id":"x","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"partial"},"finish_reason":"length"}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}"""

        let response = protocol.ParseResponse(body)
        Assert.Equal(FinishReason.Length, response.FinishReason)

    [<Fact>]
    member _.``parse invalid json throws AiException`` () =
        let action = Action(fun () -> protocol.ParseResponse("not json") |> ignore)
        Assert.Throws<AiException>(action) |> ignore

    [<Fact>]
    member _.``blank stream lines are skipped`` () =
        Assert.Null(protocol.TryParseStreamLine(""))
        Assert.Null(protocol.TryParseStreamLine("   "))

    [<Fact>]
    member _.``comment lines are skipped`` () =
        Assert.Null(protocol.TryParseStreamLine(": ping"))

    [<Fact>]
    member _.``done marker is skipped`` () =
        Assert.Null(protocol.TryParseStreamLine("data: [DONE]"))

    [<Fact>]
    member _.``delta content yields text delta`` () =
        let event =
            protocol.TryParseStreamLine(
                """data: {"id":"chatcmpl-x","choices":[{"index":0,"delta":{"content":"Hel"},"finish_reason":null}]}"""
            )

        let delta = Assert.IsType<TextDelta>(event)
        Assert.Equal("text-delta", delta.Kind)
        Assert.Equal("Hel", delta.Text)

    [<Fact>]
    member _.``tool call chunk yields tool call delta`` () =
        let event =
            protocol.TryParseStreamLine(
                """data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"ci"}}]},"finish_reason":null}]}"""
            )

        let delta = Assert.IsType<ToolCallDelta>(event)
        Assert.Equal("tool-call-delta", delta.Kind)
        Assert.Equal(0, delta.Index)
        Assert.Equal("call_1", delta.Id)
        Assert.Equal("get_weather", delta.Name)
        Assert.Equal("""{"ci""", delta.ArgumentsDelta)

    [<Fact>]
    member _.``usage chunk yields usage update`` () =
        let event =
            protocol.TryParseStreamLine(
                """data: {"id":"chatcmpl-x","choices":[],"usage":{"prompt_tokens":5,"completion_tokens":6,"total_tokens":11}}"""
            )

        let update = Assert.IsType<UsageUpdate>(event)
        Assert.Equal("usage", update.Kind)
        Assert.Equal(5L, update.Usage.InputTokens)
        Assert.Equal(6L, update.Usage.OutputTokens)
        Assert.Equal(11L, update.Usage.TotalTokens)

    [<Fact>]
    member _.``finish reason chunk yields completed`` () =
        let event =
            protocol.TryParseStreamLine(
                """data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}"""
            )

        let completed = Assert.IsType<Completed>(event)
        Assert.Equal("completed", completed.Kind)
        Assert.Equal(FinishReason.Stop, completed.FinishReason)

    [<Fact>]
    member _.``ChatClient complete round trips through transport`` () : Task =
        task {
            let body =
                """{"id":"chatcmpl-1","model":"gpt-5","choices":[{"index":0,"message":{"role":"assistant","content":"Hello!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":4,"total_tokens":7}}"""

            let transport = FakeTransport(TransportResponse(200, body), [||])
            let headers = Dictionary<string, string>() :> IReadOnlyDictionary<string, string>

            let client =
                ChatClient("gpt-5", protocol, transport, "https://example.test/v1/chat/completions", headers)

            let request = ChatRequest("gpt-5", [| ChatMessage.User "hi" |])
            let! response = client.Complete(request, CancellationToken.None)

            Assert.Equal("chatcmpl-1", response.Id)
            Assert.Equal("gpt-5", response.Model)
            Assert.Equal("Hello!", response.Message.Text)
            Assert.Equal(FinishReason.Stop, response.FinishReason)
            Assert.Equal(3L, response.Usage.InputTokens)
        }

    [<Fact>]
    member _.``ChatClient stream collects events`` () : Task =
        task {
            let lines =
                [| """data: {"choices":[{"index":0,"delta":{"content":"Hel"},"finish_reason":null}]}"""
                   """data: {"choices":[{"index":0,"delta":{"content":"lo"},"finish_reason":null}]}"""
                   """data: {"choices":[],"usage":{"prompt_tokens":2,"completion_tokens":2,"total_tokens":4}}"""
                   """data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""
                   "data: [DONE]" |]

            let transport = FakeTransport(TransportResponse(200, ""), lines)
            let headers = Dictionary<string, string>() :> IReadOnlyDictionary<string, string>

            let client =
                ChatClient("gpt-5", protocol, transport, "https://example.test/v1/chat/completions", headers)

            let request = ChatRequest("gpt-5", [| ChatMessage.User "hi" |])
            let events = ResizeArray<ChatStreamEvent>()
            let enumerator = client.Stream(request, CancellationToken.None).GetAsyncEnumerator(CancellationToken.None)
            let mutable more = true

            try
                while more do
                    let! moved = enumerator.MoveNextAsync().AsTask()
                    more <- moved

                    if moved then
                        events.Add(enumerator.Current)
            finally
                enumerator.DisposeAsync() |> ignore

            Assert.Equal(4, events.Count)

            let first = Assert.IsType<TextDelta>(events.[0])
            Assert.Equal("Hel", first.Text)

            let second = Assert.IsType<TextDelta>(events.[1])
            Assert.Equal("lo", second.Text)

            Assert.IsType<UsageUpdate>(events.[2]) |> ignore

            let completed = Assert.IsType<Completed>(events.[3])
            Assert.Equal(FinishReason.Stop, completed.FinishReason)
        }

    [<Fact>]
    member _.``ChatClient non success status throws AiException`` () : Task =
        task {
            let transport =
                FakeTransport(TransportResponse(500, """{"error":"boom"}"""), [||])

            let headers = Dictionary<string, string>() :> IReadOnlyDictionary<string, string>

            let client =
                ChatClient("gpt-5", protocol, transport, "https://example.test/v1/chat/completions", headers)

            let request = ChatRequest("gpt-5", [| ChatMessage.User "hi" |])

            let action =
                Func<Task>(fun () -> client.Complete(request, CancellationToken.None))

            let! ex = Assert.ThrowsAsync<AiException>(action)

            Assert.True(ex.StatusCode.HasValue)
            Assert.Equal(500, ex.StatusCode.Value)
            Assert.Equal("""HTTP 500: {"error":"boom"}""", ex.Message)
        }
