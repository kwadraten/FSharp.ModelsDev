namespace FSharp.ModelsDev.Tests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog
open FSharp.ModelsDev.Providers
open FSharp.ModelsDev.MEAi
open Microsoft.Extensions.AI

type private RoundTripTransport(lines: string[], response: string) =
    let mutable body = ""
    member _.LastBody = body
    interface IChatTransport with
        member _.Post(_, _, requestBody, _) =
            body <- requestBody
            Task.FromResult(TransportResponse(200, response))
        member _.PostStream(_, _, requestBody, _) =
            body <- requestBody
            SeqAsyncEnumerable<string>(lines) :> IAsyncEnumerable<string>

type BackendMeaiRoundTripTests() =
    [<Theory>]
    [<InlineData("google", "gemini", "functionResponse")>]
    [<InlineData("antigravity", "antigravity", "functionResponse")>]
    [<InlineData("anthropic", "anthropic-messages", "tool_result")>]
    [<InlineData("openai", "openai-responses", "function_call_output")>]
    member _.``native streamed tool and protected state survive MEAI aggregation and next request`` (provider: string, protocol: string, resultType: string) : Task = task {
        let googleResponse = """{"candidates":[{"content":{"role":"model","parts":[{"text":"checking"},{"functionCall":{"id":"call","name":"lookup","args":{"x":1}},"thoughtSignature":"signature-opaque"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":2,"candidatesTokenCount":3,"totalTokenCount":5}}"""
        let anthropicResponse = """{"id":"msg","content":[{"type":"text","text":"checking"},{"type":"thinking","thinking":"internal","signature":"signature-opaque"},{"type":"tool_use","id":"call","name":"lookup","input":{"x":1}}],"stop_reason":"tool_use","usage":{"input_tokens":2,"output_tokens":3}}"""
        let openaiResponse = """{"id":"resp","status":"completed","output":[{"type":"reasoning","id":"reason","encrypted_content":"signature-opaque"},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"checking"}]},{"type":"function_call","call_id":"call","name":"lookup","arguments":"{\"x\":1}"}],"usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5}}"""
        let frame eventName data = [| "event: " + eventName; "data: " + data; "" |]
        let response, lines =
            match provider with
            | "anthropic" ->
                anthropicResponse,
                [| frame "message_start" """{"type":"message_start","message":{"usage":{"input_tokens":2,"output_tokens":0}}}"""
                   frame "content_block_start" """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""
                   frame "content_block_delta" """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"checking"}}"""
                   frame "content_block_stop" """{"type":"content_block_stop","index":0}"""
                   frame "content_block_start" """{"type":"content_block_start","index":1,"content_block":{"type":"thinking","thinking":"internal","signature":""}}"""
                   frame "content_block_delta" """{"type":"content_block_delta","index":1,"delta":{"type":"signature_delta","signature":"signature-opaque"}}"""
                   frame "content_block_stop" """{"type":"content_block_stop","index":1}"""
                   frame "content_block_start" """{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"call","name":"lookup","input":{}}}"""
                   frame "content_block_delta" """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"x\":1}"}}"""
                   frame "content_block_stop" """{"type":"content_block_stop","index":2}"""
                   frame "message_delta" """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":3}}"""
                   frame "message_stop" """{"type":"message_stop"}""" |] |> Array.concat
            | "openai" ->
                openaiResponse,
                [| frame "response.output_text.delta" """{"type":"response.output_text.delta","delta":"checking"}"""
                   frame "response.output_item.added" """{"type":"response.output_item.added","output_index":2,"item":{"type":"function_call","call_id":"call","name":"lookup"}}"""
                   frame "response.function_call_arguments.delta" """{"type":"response.function_call_arguments.delta","output_index":2,"delta":"{\"x\":1}"}"""
                   frame "response.completed" ("{\"type\":\"response.completed\",\"response\":" + openaiResponse + "}") |] |> Array.concat
            | "antigravity" ->
                let wrapped = "{\"response\":" + googleResponse + "}"
                wrapped, [| "data: " + wrapped; "" |]
            | _ -> googleResponse, [| "data: " + googleResponse; "" |]
        let transport = RoundTripTransport(lines, response)
        let factory = ProviderFactory(SnapshotCatalog(), ApiKeyCredentialProvider("test-token"), null, transport)
        let options = ProviderOptions(ProtocolId = protocol, ProjectId = "project")
        let model = factory.CreateClient(provider, "account", "test-model", options)
        use client = model.AsIChatClient()
        let user = Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "lookup")
        let! aggregated = client.GetStreamingResponseAsync([| user |], null, CancellationToken.None).ToChatResponseAsync(CancellationToken.None)
        let assistant = Assert.Single(aggregated.Messages)
        Assert.Equal("checking", assistant.Text)
        let call = Assert.Single(assistant.Contents |> Seq.choose (function :? Microsoft.Extensions.AI.FunctionCallContent as item -> Some item | _ -> None))
        Assert.Equal("call", call.CallId)
        Assert.Equal("lookup", call.Name)
        Assert.Equal("1", string call.Arguments.["x"])
        Assert.Equal(Nullable 2L, aggregated.Usage.InputTokenCount)
        Assert.Equal(Nullable 3L, aggregated.Usage.OutputTokenCount)
        Assert.Contains("signature-opaque", assistant.AdditionalProperties.["FSharp.ModelsDev.ProviderState"] :?> string)
        let result = Microsoft.Extensions.AI.FunctionResultContent("call", "answer") :> Microsoft.Extensions.AI.AIContent
        let toolMessage = Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Tool, [| result |])
        let! _ = client.GetResponseAsync([| user; assistant; toolMessage |], null, CancellationToken.None)
        Assert.Contains("signature-opaque", transport.LastBody)
        Assert.Contains(resultType, transport.LastBody)
        Assert.Contains("answer", transport.LastBody)
    }
