namespace FSharp.ModelsDev.Tests

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.ModelsDev
open FSharp.ModelsDev.MEAi
open Microsoft.Extensions.AI

type private ResponseCredentials() =
    interface ICredentialProvider with
        member _.GetHeaders(_accountId, _ct) =
            Task.FromResult(
                Dictionary<string, string>(dict [ "Authorization", "Bearer test-key" ])
                :> IReadOnlyDictionary<string, string>
            )

type private CompletionTransport(lines: string[]) =
    let mutable postCount = 0
    let mutable streamCount = 0
    let mutable disposed = 0
    let mutable body = ""
    member _.PostCount = postCount
    member _.StreamCount = streamCount
    member _.Disposed = disposed
    member _.Body = body
    interface IChatTransport with
        member _.Post(_url, _headers, requestBody, _ct) =
            postCount <- postCount + 1
            body <- requestBody
            Task.FromResult(TransportResponse(200, "{}"))
        member _.PostStream(_url, _headers, requestBody, _ct) =
            streamCount <- streamCount + 1
            body <- requestBody
            { new IAsyncEnumerable<string> with
                member _.GetAsyncEnumerator(_ct) =
                    let mutable index = -1
                    { new IAsyncEnumerator<string> with
                        member _.Current = lines[index]
                        member _.MoveNextAsync() =
                            index <- index + 1
                            ValueTask<bool>(index < lines.Length)
                        member _.DisposeAsync() =
                            disposed <- disposed + 1
                            ValueTask() } }

type OpenAIResponsesTests() =

    let backend () =
        OpenAIResponsesBackend("gpt-5", "test-account", ResponseCredentials(), ProviderOptions())

    [<Fact>]
    member _.``prepare writes Responses state images tools and per-request auth`` () : Task =
        task {
            let clientBackend = backend () :> IChatBackend
            let imageMessage =
                FSharp.ModelsDev.ChatMessage(
                    FSharp.ModelsDev.Role.User,
                    [| TextPart("Look") :> ContentPart; ImagePart("https://example.test/cat.png", "image/png") :> ContentPart |]
                )
            let request = ChatRequest("gpt-5", [| imageMessage |])
            request.Tools <- [| ToolDefinition("weather", "Current weather", """{"type":"object"}""") |]
            request.ReasoningEffort <- "medium"
            let! prepared = clientBackend.PrepareRequest(request, false, CancellationToken.None)
            Assert.EndsWith("/v1/responses", prepared.Url)
            Assert.Equal("Bearer test-key", prepared.Headers["Authorization"])
            use doc = JsonDocument.Parse prepared.Body
            let root = doc.RootElement
            Assert.False((root.GetProperty("store").GetBoolean()))
            Assert.Equal("gpt-5", (root.GetProperty("model").GetString()))
            Assert.Equal("reasoning.encrypted_content", root.GetProperty("include").[0].GetString())
            Assert.Equal("input_image", (root.GetProperty("input").[0].GetProperty("content").[1].GetProperty("type").GetString()))
            Assert.Equal("function", (root.GetProperty("tools").[0].GetProperty("type").GetString()))
            Assert.Equal("medium", (root.GetProperty("reasoning").GetProperty("effort").GetString()))
        }

    [<Fact>]
    member _.``parse response carries encrypted reasoning and tool calls into next stateless input`` () : Task =
        task {
            let clientBackend = backend () :> IChatBackend
            let body =
                """{"id":"resp_1","model":"gpt-5-2026-10-15","status":"completed","output":[{"type":"reasoning","id":"rs_1","encrypted_content":"opaque"},{"type":"message","content":[{"type":"output_text","text":"Done"}]},{"type":"function_call","call_id":"call_1","name":"weather","arguments":"{\"city\":\"Oslo\"}"}],"usage":{"input_tokens":8,"output_tokens":4,"total_tokens":12}}"""
            let response = clientBackend.ParseResponse body
            Assert.Equal("Done", response.Message.Text)
            Assert.Equal("gpt-5-2026-10-15", response.Model)
            Assert.Equal(FinishReason.ToolCalls, response.FinishReason)
            Assert.Equal(12L, response.Usage.TotalTokens)
            Assert.Single(response.Message.ToolCalls) |> ignore
            Assert.Contains("encrypted_content", response.Message.ProviderStateJson)

            let next = ChatRequest("gpt-5", [| ChatMessage.User("Look"); response.Message; ChatMessage.ToolResult("call_1", "sunny") |])
            let! prepared = clientBackend.PrepareRequest(next, false, CancellationToken.None)
            use nextDoc = JsonDocument.Parse prepared.Body
            let input = nextDoc.RootElement.GetProperty("input")
            Assert.Equal("reasoning", input[1].GetProperty("type").GetString())
            Assert.Equal("function_call", input[3].GetProperty("type").GetString())
            Assert.Equal("function_call_output", input[4].GetProperty("type").GetString())
            Assert.Equal("opaque", input[1].GetProperty("encrypted_content").GetString())
        }

    [<Fact>]
    member _.``stateless reasoning state is rejected for another model`` () =
        let clientBackend = backend () :> IChatBackend
        let message = ChatMessage.Assistant("old")
        message.ProviderStateJson <- """{"provider":"openai-responses","model":"gpt-4.1","items":[]}"""
        let request = ChatRequest("gpt-5", [| message |])
        let action = Action(fun () -> clientBackend.PrepareRequest(request, false, CancellationToken.None).GetAwaiter().GetResult() |> ignore)
        Assert.Throws<AiException>(action) |> ignore

    [<Fact>]
    member _.``MEAI streamed calls state and usage round trip through a complete assistant message`` () : Task =
        task {
            let stateJson = """{"provider":"openai-responses","model":"gpt-5","items":[{"type":"reasoning","id":"rs_1","encrypted_content":"opaque"}]}"""
            let events =
                [| ToolCallDelta(0, "call_weather", "weather", "{\"city\":") :> ChatStreamEvent
                   ToolCallDelta(0, null, null, "\"Oslo\"}") :> ChatStreamEvent
                   TextDelta("I’ll check the weather.") :> ChatStreamEvent
                   ProviderStateUpdate(stateJson) :> ChatStreamEvent
                   UsageUpdate(Usage(7L, 4L, 11L)) :> ChatStreamEvent
                   Completed(FinishReason.ToolCalls, Usage(7L, 4L, 11L)) :> ChatStreamEvent |]
            let model = FakeChatModel("unused", events) :> IChatModel
            let client = model.AsIChatClient()
            let stream = client.GetStreamingResponseAsync([| Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "What is the weather in Oslo?") |], null, CancellationToken.None)
            let! response = stream.ToChatResponseAsync(CancellationToken.None)
            Assert.Single(response.Messages) |> ignore
            let assistant = response.Messages[0]
            Assert.Equal("I’ll check the weather.", assistant.Text)
            let call = assistant.Contents |> Seq.choose (function :? FunctionCallContent as content -> Some content | _ -> None) |> Seq.exactlyOne
            Assert.Equal("call_weather", call.CallId)
            Assert.Equal("Oslo", (call.Arguments["city"] :?> System.Text.Json.JsonElement).GetString())
            let storedState = assistant.AdditionalProperties["FSharp.ModelsDev.ProviderState"] :?> string
            use stateDocument = JsonDocument.Parse(storedState)
            Assert.Equal("opaque", stateDocument.RootElement.GetProperty("items").[0].GetProperty("encrypted_content").GetString())
            Assert.Equal(Nullable 7L, response.Usage.InputTokenCount)
            Assert.Equal(Nullable 4L, response.Usage.OutputTokenCount)

            let contractRequest = Conversions.requestFromAi "gpt-5" response.Messages null false
            let contractMessage = contractRequest.Messages[0]
            Assert.Equal(stateJson, contractMessage.ProviderStateJson)
            Assert.Equal("call_weather", contractMessage.ToolCalls[0].Id)
            Assert.Equal("{\"city\":\"Oslo\"}", contractMessage.ToolCalls[0].ArgumentsJson)
            // Re-enumerating the same stream must get fresh argument and message state.
            let! second = stream.ToChatResponseAsync(CancellationToken.None)
            let secondCall = second.Messages[0].Contents |> Seq.choose (function :? FunctionCallContent as content -> Some content | _ -> None) |> Seq.exactlyOne
            Assert.Equal("Oslo", (secondCall.Arguments["city"] :?> System.Text.Json.JsonElement).GetString())
            Assert.Equal(Nullable 7L, second.Usage.InputTokenCount)
        }

    [<Fact>]
    member _.``stream parser combines response events and emits EOF completed usage and provider state`` () : Task =
        task {
            let lines =
                [| "event: response.created"
                   "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_stream\",\"model\":\"gpt-5\"}}"
                   ""
                   "event: response.output_text.delta"
                   "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hello\"}"
                   ""
                   "event: response.output_item.added"
                   "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"weather\"}}"
                   ""
                   "event: response.output_item.added"
                   "data: {\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{\"type\":\"function_call\",\"call_id\":\"call_2\",\"name\":\"lookup_country\"}}"
                   ""
                   "event: response.function_call_arguments.delta"
                   "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"delta\":\"{\\\"city\\\":\"}"
                   ""
                   "event: response.function_call_arguments.delta"
                   "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":1,\"delta\":\"{\\\"country\\\":\"}"
                   ""
                   "event: response.function_call_arguments.delta"
                   "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"delta\":\"\\\"Oslo\\\"}\"}"
                   ""
                   "event: response.function_call_arguments.delta"
                   "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":1,\"delta\":\"\\\"NO\\\"}\"}"
                   ""
                   "event: response.output_item.done"
                   "data: {\"type\":\"response.output_item.done\",\"output_index\":3,\"item\":{\"type\":\"reasoning\",\"id\":\"rs_1\",\"encrypted_content\":\"opaque\"}}"
                   ""
                   "event: response.completed"
                   "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_stream\",\"model\":\"gpt-5\",\"status\":\"completed\",\"output\":[{\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"weather\",\"arguments\":\"{\\\"city\\\":\\\"Oslo\\\"}\"},{\"type\":\"function_call\",\"call_id\":\"call_2\",\"name\":\"lookup_country\",\"arguments\":\"{\\\"country\\\":\\\"NO\\\"}\"},{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hello\"}]},{\"type\":\"reasoning\",\"id\":\"rs_1\",\"encrypted_content\":\"opaque\"}],\"usage\":{\"input_tokens\":2,\"output_tokens\":3,\"total_tokens\":5}}}" |]
            let transport = FakeTransport(TransportResponse(200, ""), lines)
            let chatClient = ChatClient("gpt-5", backend () :> IChatBackend, transport :> IChatTransport)
            let stream = chatClient.Stream(ChatRequest("gpt-5", [| ChatMessage.User("hi") |]), CancellationToken.None)
            let events = ResizeArray<ChatStreamEvent>()
            let iterator = stream.GetAsyncEnumerator(CancellationToken.None)
            let mutable more = true
            try
                while more do
                    let! moved = iterator.MoveNextAsync().AsTask()
                    more <- moved
                    if moved then events.Add(iterator.Current)
            finally
                iterator.DisposeAsync() |> ignore
            Assert.Contains(events, fun event -> event :? TextDelta)
            let toolEvents = events |> Seq.choose (fun event -> match event with :? ToolCallDelta as delta -> Some delta | _ -> None) |> Seq.toArray
            Assert.Equal(4, toolEvents.Length)
            Assert.Equal(2, toolEvents |> Array.filter (fun event -> event.Id = "call_1") |> Array.length)
            Assert.Equal(2, toolEvents |> Array.filter (fun event -> event.Id = "call_2") |> Array.length)
            let state = events |> Seq.choose (fun event -> match event with :? ProviderStateUpdate as update -> Some update | _ -> None) |> Seq.last
            Assert.Contains("encrypted_content", state.StateJson)
            use stateDoc = JsonDocument.Parse(state.StateJson)
            Assert.Equal(4, stateDoc.RootElement.GetProperty("items").GetArrayLength())
            Assert.Equal("function_call", stateDoc.RootElement.GetProperty("items").[0].GetProperty("type").GetString())
            Assert.Equal("function_call", stateDoc.RootElement.GetProperty("items").[1].GetProperty("type").GetString())
            let completed = events |> Seq.choose (fun event -> match event with :? Completed as value -> Some value | _ -> None) |> Seq.last
            Assert.Equal(5L, completed.Usage.TotalTokens)
        }

    [<Fact>]
    member _.``Responses stream rejects truncated endings and emits terminal completion once`` () =
        let clientBackend = backend () :> IChatBackend
        let truncated = clientBackend.CreateStreamParser()
        truncated.PushLine("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_partial\"}}") |> ignore
        truncated.PushLine("") |> ignore
        Assert.Throws<AiException>(Action(fun () -> truncated.Finish() |> ignore)) |> ignore

        let complete = clientBackend.CreateStreamParser()
        complete.PushLine("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_done\",\"model\":\"gpt-5\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":2,\"total_tokens\":3}}}") |> ignore
        let first = complete.PushLine("")
        Assert.Single(first) |> ignore
        complete.PushLine("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_done\",\"model\":\"gpt-5\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":2,\"total_tokens\":3}}}") |> ignore
        Assert.Empty(complete.PushLine(""))
        Assert.Empty(complete.Finish())

    [<Fact>]
    member _.``Codex completion uses stream and returns authoritative response`` () : Task =
        task {
            let options = ProviderOptions()
            options.ProtocolId <- "codex-responses"
            let codexBackend = OpenAIResponsesBackend("gpt-5", "test-account", ResponseCredentials(), options)
            Assert.True((codexBackend :> IStreamingCompletionBackend).UseStreamingForCompletions)
            let lines =
                [| "event: response.completed"
                   "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_final\",\"model\":\"gpt-5-2026-10-15\",\"status\":\"completed\",\"output\":[{\"type\":\"reasoning\",\"id\":\"rs_final\",\"encrypted_content\":\"sealed\"},{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hello once\"}]},{\"type\":\"function_call\",\"call_id\":\"call_final\",\"name\":\"weather\",\"arguments\":\"{\\\"city\\\":\\\"Oslo\\\"}\"}],\"usage\":{\"input_tokens\":7,\"output_tokens\":5,\"total_tokens\":12}}}" |]
            let transport = CompletionTransport(lines)
            let client = ChatClient("gpt-5", codexBackend :> IChatBackend, transport :> IChatTransport)
            let request = ChatRequest("gpt-5", [| ChatMessage.User("hi") |])
            request.MaxTokens <- Nullable 77
            let! response = client.Complete(request, CancellationToken.None)
            Assert.Equal(0, transport.PostCount)
            Assert.Equal(1, transport.StreamCount)
            Assert.Equal(1, transport.Disposed)
            use body = JsonDocument.Parse(transport.Body)
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean())
            Assert.False(body.RootElement.GetProperty("store").GetBoolean())
            Assert.Equal(77, body.RootElement.GetProperty("max_output_tokens").GetInt32())
            Assert.Equal("resp_final", response.Id)
            Assert.Equal("gpt-5-2026-10-15", response.Model)
            Assert.Equal("Hello once", response.Message.Text)
            Assert.Equal("call_final", response.Message.ToolCalls[0].Id)
            Assert.Equal(12L, response.Usage.TotalTokens)
            Assert.Contains("sealed", response.Message.ProviderStateJson)
        }

    [<Fact>]
    member _.``OpenAI Responses completion retains normal nonstream POST behavior`` () : Task =
        task {
            let transport = CompletionTransport([||])
            let client = ChatClient("gpt-5", backend () :> IChatBackend, transport :> IChatTransport)
            let! _ = client.Complete(ChatRequest("gpt-5", [| ChatMessage.User("hi") |]), CancellationToken.None)
            Assert.Equal(1, transport.PostCount)
            Assert.Equal(0, transport.StreamCount)
            use body = JsonDocument.Parse(transport.Body)
            Assert.False(body.RootElement.GetProperty("stream").GetBoolean())
        }

    [<Fact>]
    member _.``Codex completion fails on error or truncated stream and disposes transport`` () : Task =
        task {
            let options = ProviderOptions()
            options.ProtocolId <- "codex-responses"
            let codexBackend = OpenAIResponsesBackend("gpt-5", "test-account", ResponseCredentials(), options)
            Assert.True((codexBackend :> IStreamingCompletionBackend).UseStreamingForCompletions)
            let run lines =
                let transport = CompletionTransport(lines)
                let client = ChatClient("gpt-5", codexBackend :> IChatBackend, transport :> IChatTransport)
                task {
                    let! error = Assert.ThrowsAsync<AiException>(fun () -> client.Complete(ChatRequest("gpt-5", [| ChatMessage.User("hi") |]), CancellationToken.None))
                    Assert.NotNull(error)
                    Assert.Equal(1, transport.Disposed)
                }
            do! run [| "data: {\"type\":\"response.created\",\"response\":{}}"; "" |]
            do! run [| "data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"failed\"}}}"; "" |]
            use canceled = new CancellationTokenSource()
            canceled.Cancel()
            let transport = CompletionTransport([||])
            let client = ChatClient("gpt-5", codexBackend :> IChatBackend, transport :> IChatTransport)
            let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> client.Complete(ChatRequest("gpt-5", [| ChatMessage.User("hi") |]), canceled.Token))
            Assert.Equal(1, transport.Disposed)
        }

    [<Fact>]
    member _.``known ChatGPT Codex endpoint enables streamed completions`` () =
        let options = ProviderOptions()
        options.BaseUrl <- "https://chatgpt.com/backend-api/codex/"
        let codexBackend = OpenAIResponsesBackend("gpt-5", "test-account", ResponseCredentials(), options)
        Assert.True((codexBackend :> IStreamingCompletionBackend).UseStreamingForCompletions)
        Assert.False((backend () :> IStreamingCompletionBackend).UseStreamingForCompletions)

    [<Fact>]
    member _.``Codex completed response accepts nullable nested fields`` () : Task =
        task {
            let options = ProviderOptions()
            options.ProtocolId <- "codex-responses"
            let codexBackend = OpenAIResponsesBackend("gpt-5", "test-account", ResponseCredentials(), options)
            let lines =
                [| "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_nullable\",\"model\":\"gpt-5\",\"status\":\"completed\",\"incomplete_details\":null,\"error\":null,\"usage\":null,\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hello\"}]}]}}" |]
            let transport = CompletionTransport(lines)
            let client = ChatClient("gpt-5", codexBackend :> IChatBackend, transport :> IChatTransport)
            let! response = client.Complete(ChatRequest("gpt-5", [| ChatMessage.User("hi") |]), CancellationToken.None)
            Assert.Equal("resp_nullable", response.Id)
            Assert.Equal("Hello", response.Message.Text)
            Assert.Equal(0L, response.Usage.TotalTokens)
        }
