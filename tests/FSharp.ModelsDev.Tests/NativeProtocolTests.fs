namespace FSharp.ModelsDev.Tests

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.ModelsDev
open FSharp.ModelsDev.Providers

type private StaticHeaders(headers: IReadOnlyDictionary<string,string>) =
    interface ICredentialProvider with
        member _.GetHeaders(_, _) = Task.FromResult(headers)

type NativeProtocolTests() =
    let parse (s: string) = JsonDocument.Parse(s)

    [<Fact>]
    member _.``Gemini serializes roles generation options images and tools``() =
        let codec = GeminiCodec("gemini-2.5-flash", "gemini")
        let request = ChatRequest("gemini-2.5-flash", [| ChatMessage.System("rules"); ChatMessage.User("hello") |])
        request.MaxTokens <- Nullable 81
        request.ReasoningBudgetTokens <- Nullable 2048
        request.Tools <- [| ToolDefinition("lookup", "Lookup", """{"type":"object"}""") |]
        use d = parse (codec.Serialize(request))
        let root = d.RootElement
        Assert.Equal("rules", root.GetProperty("systemInstruction").GetProperty("parts").[0].GetProperty("text").GetString())
        Assert.Equal("user", root.GetProperty("contents").[0].GetProperty("role").GetString())
        Assert.Equal(81, root.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32())
        Assert.Equal(2048, root.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32())
        Assert.Equal("lookup", root.GetProperty("tools").[0].GetProperty("functionDeclarations").[0].GetProperty("name").GetString())

    [<Fact>]
    member _.``Gemini passes JSON Schema intact while Antigravity safely inlines local references``() =
        let schema = """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,"properties":{"kind":{"const":"x"},"name":{"$ref":"#/definitions/name"}},"required":["kind"],"definitions":{"name":{"type":"string"}}}"""
        let nativeRequest = ChatRequest("gemini-2.5-flash", [| ChatMessage.User("hi") |])
        nativeRequest.Tools <- [| ToolDefinition("submit", "Submit", schema) |]
        use native = parse (GeminiCodec("gemini-2.5-flash", "gemini").Serialize(nativeRequest))
        let nativeFunction = native.RootElement.GetProperty("tools").[0].GetProperty("functionDeclarations").[0]
        let mutable nativeSchema = Unchecked.defaultof<JsonElement>
        Assert.True(nativeFunction.TryGetProperty("parametersJsonSchema", &nativeSchema))

        let legacyRequest = ChatRequest("gemini-2.5-flash", [| ChatMessage.User("hi") |])
        legacyRequest.Tools <- [| ToolDefinition("submit", "Submit", schema) |]
        use legacy = parse (GeminiCodec("gemini-2.5-flash", "antigravity").Serialize(legacyRequest))
        let parameters = legacy.RootElement.GetProperty("tools").[0].GetProperty("functionDeclarations").[0].GetProperty("parameters")
        Assert.Equal("x", parameters.GetProperty("properties").GetProperty("kind").GetProperty("enum").[0].GetString())
        Assert.Equal("string", parameters.GetProperty("properties").GetProperty("name").GetProperty("type").GetString())
        let mutable schemaMetadata = Unchecked.defaultof<JsonElement>
        Assert.False(parameters.TryGetProperty("$schema", &schemaMetadata))

        let unsupportedSchemas =
            [| """{"type":"object","not":{"type":"null"}}"""
               """{"type":"object","definitions":{"node":{"$ref":"#/definitions/node"}},"properties":{"child":{"$ref":"#/definitions/node"}}}"""
               """{"type":"object","definitions":{"value":{"type":"string"}},"properties":{"name":{"$ref":"#/definitions/value","type":"number"}}}"""
               """{"type":"object","properties":{"kind":{"const":"x","enum":["x","y"]}}}""" |]
        for invalidSchema in unsupportedSchemas do
            let unsupported = ChatRequest("gemini-2.5-flash", [| ChatMessage.User("hi") |])
            unsupported.Tools <- [| ToolDefinition("bad", "Bad", invalidSchema) |]
            Assert.Throws<AiException>(Action(fun () -> GeminiCodec("gemini-2.5-flash", "antigravity").Serialize(unsupported) |> ignore)) |> ignore

    [<Fact>]
    member _.``native codecs preserve text image parts and zero one or two stop sequences``() =
        let image = ImagePart("data:image/png;base64,aGVsbG8=", "image/png")
        for count in 0 .. 2 do
            let stops = [| "stop-a"; "stop-b" |] |> Array.take count
            let gemini = GeminiCodec("gemini-2.5-flash", "gemini")
            let g = ChatRequest("gemini-2.5-flash", [| ChatMessage(Role.User, [| TextPart("caption"); image |]) |])
            if count > 0 then g.StopSequences <- stops
            use gd = parse (gemini.Serialize(g))
            let gparts = gd.RootElement.GetProperty("contents").[0].GetProperty("parts")
            Assert.Equal("caption", gparts.[0].GetProperty("text").GetString())
            Assert.Equal("image/png", gparts.[1].GetProperty("inlineData").GetProperty("mimeType").GetString())
            let mutable geminiStops = Unchecked.defaultof<JsonElement>
            if count = 0 then Assert.False(gd.RootElement.GetProperty("generationConfig").TryGetProperty("stopSequences", &geminiStops))
            else Assert.Equal(count, gd.RootElement.GetProperty("generationConfig").GetProperty("stopSequences").GetArrayLength())

            let claude = AnthropicCodec("claude-3-7-sonnet", "anthropic", false)
            let a = ChatRequest("claude-3-7-sonnet", [| ChatMessage(Role.User, [| TextPart("caption"); image |]) |])
            if count > 0 then a.StopSequences <- stops
            use ad = parse (claude.Serialize(a))
            let content = ad.RootElement.GetProperty("messages").[0].GetProperty("content")
            Assert.Equal(JsonValueKind.Array, content.ValueKind)
            Assert.Equal(2, content.GetArrayLength())
            Assert.Equal("base64", content.[1].GetProperty("source").GetProperty("type").GetString())
            let mutable claudeStops = Unchecked.defaultof<JsonElement>
            if count = 0 then Assert.False(ad.RootElement.TryGetProperty("stop_sequences", &claudeStops))
            else Assert.Equal(count, ad.RootElement.GetProperty("stop_sequences").GetArrayLength())

    [<Fact>]
    member _.``parallel Gemini and Claude tool results share one follow-up user message``() =
        let assistant = ChatMessage(Role.Assistant, [||], null, [| ToolCall("call-a", "a", "{}"); ToolCall("call-b", "b", "{}") |], null)
        let messages = [| assistant; ChatMessage.ToolResult("call-a", "1"); ChatMessage.ToolResult("call-b", "2") |]
        let gemini = GeminiCodec("gemini-2.5-flash", "gemini")
        use gd = parse (gemini.Serialize(ChatRequest("gemini-2.5-flash", messages)))
        let geminiContents = gd.RootElement.GetProperty("contents")
        Assert.Equal(2, geminiContents.GetArrayLength())
        let responses = geminiContents.[1].GetProperty("parts")
        Assert.Equal(2, responses.GetArrayLength())
        Assert.Equal("call-a", responses.[0].GetProperty("functionResponse").GetProperty("id").GetString())
        Assert.Equal("call-b", responses.[1].GetProperty("functionResponse").GetProperty("id").GetString())
        Assert.Equal(1, responses.[0].GetProperty("functionResponse").GetProperty("response").GetProperty("result").GetInt32())
        Assert.Equal(2, responses.[1].GetProperty("functionResponse").GetProperty("response").GetProperty("result").GetInt32())

        let claude = AnthropicCodec("claude-3-7-sonnet", "anthropic", false)
        use ad = parse (claude.Serialize(ChatRequest("claude-3-7-sonnet", messages)))
        let claudeMessages = ad.RootElement.GetProperty("messages")
        Assert.Equal(2, claudeMessages.GetArrayLength())
        let results = claudeMessages.[1].GetProperty("content")
        Assert.Equal(2, results.GetArrayLength())
        Assert.Equal("call-a", results.[0].GetProperty("tool_use_id").GetString())
        Assert.Equal("call-b", results.[1].GetProperty("tool_use_id").GetString())

    [<Fact>]
    member _.``native decoders surface provider errors and Gemini prompt safety blocks``() =
        let gemini = GeminiCodec("gemini-2.5-flash", "gemini")
        let blocked = gemini.ParseResponseBody("""{"promptFeedback":{"blockReason":"SAFETY"},"candidates":[]}""")
        Assert.Equal(FinishReason.ContentFilter, blocked.FinishReason)
        let googleError = Action(fun () -> gemini.ParseResponseBody("""{"error":{"message":"invalid key"}}""") |> ignore)
        Assert.Throws<AiException>(googleError) |> ignore

        let claude = AnthropicCodec("claude-3-7-sonnet", "anthropic", false)
        let claudeError = Action(fun () -> claude.ParseResponseBody("""{"type":"error","error":{"message":"overloaded"}}""") |> ignore)
        Assert.Throws<AiException>(claudeError) |> ignore

    [<Fact>]
    member _.``Gemini parses text parallel tools usage and wrapped response``() =
        let codec = GeminiCodec("gemini-2.5-flash", "antigravity")
        let body = """{"response":{"responseId":"r1","modelVersion":"gemini-2.5-flash","candidates":[{"content":{"role":"model","parts":[{"text":"ok"},{"functionCall":{"id":"fc1","name":"one","args":{"a":1}}},{"functionCall":{"name":"two","args":{}}}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":8,"totalTokenCount":13}}}"""
        let response = codec.ParseResponseBody(body)
        Assert.Equal("r1", response.Id)
        Assert.Equal("ok", response.Message.Text)
        Assert.Equal(2, response.Message.ToolCalls.Count)
        Assert.StartsWith("gemini_", response.Message.ToolCalls.[1].Id)
        Assert.Equal(FinishReason.ToolCalls, response.FinishReason)
        Assert.Equal(13L, response.Usage.TotalTokens)
        use state = parse response.Message.ProviderStateJson
        Assert.Equal("antigravity", state.RootElement.GetProperty("protocol").GetString())
        Assert.Equal("gemini-2.5-flash", state.RootElement.GetProperty("model").GetString())

        let replay = ChatRequest("gemini-2.5-flash", [| ChatMessage.User("continue"); response.Message |])
        use replayDoc = parse (codec.Serialize(replay))
        let replayParts = replayDoc.RootElement.GetProperty("contents").[1].GetProperty("parts")
        Assert.Equal("fc1", replayParts.[1].GetProperty("functionCall").GetProperty("id").GetString())
        let syntheticCall = response.Message.ToolCalls.[1]
        let toolReplay = ChatRequest("gemini-2.5-flash", [| response.Message; ChatMessage.ToolResult(syntheticCall.Id, "true") |])
        use toolReplayDoc = parse (codec.Serialize(toolReplay))
        let replayFunctionCall = toolReplayDoc.RootElement.GetProperty("contents").[0].GetProperty("parts").[2].GetProperty("functionCall")
        let mutable syntheticId = Unchecked.defaultof<JsonElement>
        Assert.False(replayFunctionCall.TryGetProperty("id", &syntheticId))
        let result = toolReplayDoc.RootElement.GetProperty("contents").[1].GetProperty("parts").[0].GetProperty("functionResponse")
        Assert.False(result.TryGetProperty("id", &syntheticId))

    [<Fact>]
    member _.``Gemini stream emits every part and one completion with usage``() =
        let codec = GeminiCodec("gemini-2.5-flash", "gemini")
        let parser = codec.MakeStreamParser()
        let lines = [| "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"internal\",\"thought\":true,\"thoughtSignature\":\"sig-g\"},{\"text\":\"a\"},{\"text\":\"b\"},{\"functionCall\":{\"id\":\"fc\",\"name\":\"lookup\",\"args\":{\"q\":1}}}]}}]}"; ""; "data: {\"candidates\":[{\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":2,\"candidatesTokenCount\":3}}"; "" |]
        let events = ResizeArray<ChatStreamEvent>()
        for line in lines do events.AddRange(parser.PushLine(line))
        events.AddRange(parser.Finish())
        Assert.Equal(6, events.Count)
        Assert.Equal("a", Assert.IsType<TextDelta>(events.[0]).Text)
        Assert.Equal("b", Assert.IsType<TextDelta>(events.[1]).Text)
        Assert.IsType<ToolCallDelta>(events.[2]) |> ignore
        Assert.IsType<UsageUpdate>(events.[3]) |> ignore
        let state = Assert.IsType<ProviderStateUpdate>(events.[4])
        use stateDoc = parse state.StateJson
        Assert.Equal("sig-g", stateDoc.RootElement.GetProperty("parts").[0].GetProperty("thoughtSignature").GetString())
        Assert.IsType<Completed>(events.[5]) |> ignore
        Assert.Equal(FinishReason.ToolCalls, (Assert.IsType<Completed>(events.[5])).FinishReason)

    [<Fact>]
    member _.``Anthropic serializes system max tokens tool and thinking``() =
        let codec = AnthropicCodec("claude-3-7-sonnet", "anthropic", false)
        let request = ChatRequest("claude-3-7-sonnet", [| ChatMessage.System("policy"); ChatMessage.User("hi") |])
        request.ReasoningBudgetTokens <- Nullable 1000
        request.MaxTokens <- Nullable 2000
        request.Tools <- [| ToolDefinition("search", "Search", """{"type":"object","properties":{}}""") |]
        use d = parse (codec.Serialize(request))
        let root = d.RootElement
        Assert.Equal("claude-3-7-sonnet", root.GetProperty("model").GetString())
        Assert.Equal(2000, root.GetProperty("max_tokens").GetInt32())
        Assert.Equal("policy", root.GetProperty("system").GetString())
        Assert.Equal("enabled", root.GetProperty("thinking").GetProperty("type").GetString())
        Assert.Equal(1000, root.GetProperty("thinking").GetProperty("budget_tokens").GetInt32())
        Assert.Equal("search", root.GetProperty("tools").[0].GetProperty("name").GetString())

    [<Fact>]
    member _.``Claude Opus 4.5 accepts effort alongside an explicit thinking budget``() =
        let codec = AnthropicCodec("claude-opus-4-5", "anthropic", false)
        let request = ChatRequest("claude-opus-4-5", [| ChatMessage.User("think") |])
        request.ReasoningBudgetTokens <- Nullable 1200
        request.ReasoningEffort <- "high"
        request.MaxTokens <- Nullable 2000
        use doc = parse (codec.Serialize(request))
        Assert.Equal(1200, doc.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32())
        Assert.Equal("high", doc.RootElement.GetProperty("output_config").GetProperty("effort").GetString())

    [<Fact>]
    member _.``Anthropic response preserves thinking state and parses tool and usage``() =
        let codec = AnthropicCodec("claude-3-7-sonnet", "anthropic", false)
        let response = codec.ParseResponseBody("""{"id":"msg_1","model":"claude-3-7-sonnet","content":[{"type":"thinking","thinking":"hidden","signature":"sig"},{"type":"text","text":"answer"},{"type":"tool_use","id":"t1","name":"lookup","input":{"x":1}}],"stop_reason":"tool_use","usage":{"input_tokens":6,"cache_read_input_tokens":2,"cache_creation_input_tokens":3,"output_tokens":9}}""")
        Assert.Equal("answer", response.Message.Text)
        Assert.Equal(1, response.Message.ToolCalls.Count)
        Assert.Equal(FinishReason.ToolCalls, response.FinishReason)
        Assert.Equal(20L, response.Usage.TotalTokens)
        use state = parse response.Message.ProviderStateJson
        Assert.Equal("anthropic", state.RootElement.GetProperty("protocol").GetString())
        Assert.Equal("claude-3-7-sonnet", state.RootElement.GetProperty("model").GetString())
        let replay = ChatRequest("claude-3-7-sonnet", [| ChatMessage.User("continue"); response.Message |])
        use replayDoc = parse (codec.Serialize(replay))
        Assert.Equal("sig", replayDoc.RootElement.GetProperty("messages").[1].GetProperty("content").[0].GetProperty("signature").GetString())

    [<Fact>]
    member _.``Anthropic event parser assembles tool input deltas and terminal``() =
        let codec = AnthropicCodec("claude-3-7-sonnet", "anthropic", false)
        let parser = codec.MakeStreamParser()
        let lines = [| "event: content_block_start"; "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"reason\"}}"; ""; "event: content_block_delta"; "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"sig-c\"}}"; ""; "event: content_block_stop"; "data: {\"type\":\"content_block_stop\",\"index\":0}"; ""; "event: content_block_start"; "data: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"tool1\",\"name\":\"search\",\"input\":{}}}"; ""; "event: content_block_delta"; "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"q\\\":1}\"}}"; ""; "event: content_block_stop"; "data: {\"type\":\"content_block_stop\",\"index\":1}"; ""; "event: message_delta"; "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":4}}"; ""; "event: message_stop"; "data: {\"type\":\"message_stop\"}"; "" |]
        let events = ResizeArray<ChatStreamEvent>()
        for line in lines do events.AddRange(parser.PushLine(line))
        events.AddRange(parser.Finish())
        Assert.Equal(5, events.Count)
        let start = Assert.IsType<ToolCallDelta>(events.[0])
        Assert.Equal("tool1", start.Id)
        let delta = Assert.IsType<ToolCallDelta>(events.[1])
        Assert.Equal("{\"q\":1}", delta.ArgumentsDelta)
        Assert.IsType<UsageUpdate>(events.[2]) |> ignore
        let state = Assert.IsType<ProviderStateUpdate>(events.[3])
        use stateDoc = parse state.StateJson
        Assert.Equal("sig-c", stateDoc.RootElement.GetProperty("content").[0].GetProperty("signature").GetString())
        Assert.Equal(1, stateDoc.RootElement.GetProperty("content").[1].GetProperty("input").GetProperty("q").GetInt32())
        let replay = ChatRequest("claude-3-7-sonnet", [| ChatMessage.User("again"); ChatMessage(Role.Assistant, Array.empty<ContentPart>) |])
        replay.Messages.[1].ProviderStateJson <- state.StateJson
        use replayDoc = parse (codec.Serialize(replay))
        Assert.Equal("sig-c", replayDoc.RootElement.GetProperty("messages").[1].GetProperty("content").[0].GetProperty("signature").GetString())
        let doneEvent = Assert.IsType<Completed>(events.[4])
        Assert.Equal(FinishReason.ToolCalls, doneEvent.FinishReason)

    [<Fact>]
    member _.``backend endpoints use native and Vertex paths and fetch credentials per request``() : Task =
        task {
            let h = Dictionary<string,string>()
            h["x-goog-api-key"] <- "secret"
            let credentials = StaticHeaders(h :> IReadOnlyDictionary<string,string>) :> ICredentialProvider
            let gemini = GeminiBackend("gemini-2.5-flash", "acct", credentials, ProviderOptions()) :> IChatBackend
            let! prepared = gemini.PrepareRequest(ChatRequest("gemini-2.5-flash", [| ChatMessage.User("hi") |]), true, CancellationToken.None)
            Assert.Contains("models/gemini-2.5-flash:streamGenerateContent?alt=sse", prepared.Url)
            Assert.Equal("secret", prepared.Headers.["x-goog-api-key"])
            let opts = ProviderOptions()
            opts.ProjectId <- "p"
            opts.Location <- "global"
            let vertex = VertexAnthropicBackend("claude-3-7-sonnet", "acct", null, opts) :> IChatBackend
            let! claude = vertex.PrepareRequest(ChatRequest("claude-3-7-sonnet", [| ChatMessage.User("hi") |]), false, CancellationToken.None)
            Assert.Contains("aiplatform.googleapis.com/v1/projects/p/locations/global/publishers/anthropic/models/claude-3-7-sonnet:rawPredict", claude.Url)
            use d = parse claude.Body
            Assert.Equal("vertex-2023-10-16", d.RootElement.GetProperty("anthropic_version").GetString())
            let mutable modelProperty = Unchecked.defaultof<JsonElement>
            Assert.False(d.RootElement.TryGetProperty("model", &modelProperty))
        }

    [<Fact>]
    member _.``Anthropic request bodies remain valid with streaming thinking and multiple stops``() : Task =
        task {
            let options = ProviderOptions()
            options.ProjectId <- "p"
            options.Location <- "global"
            let native = AnthropicBackend("claude-4-6-sonnet", "acct", null, ProviderOptions()) :> IChatBackend
            let vertex = VertexAnthropicBackend("claude-4-6-sonnet", "acct", null, options) :> IChatBackend
            for backend in [| native; vertex |] do
                for streaming in [| false; true |] do
                    for thinking in [| false; true |] do
                        let request = ChatRequest("claude-4-6-sonnet", [| ChatMessage.User("hello") |])
                        request.StopSequences <- [| "stop-a"; "stop-b" |]
                        if thinking then request.ReasoningEffort <- "high"
                        let! prepared = backend.PrepareRequest(request, streaming, CancellationToken.None)
                        use body = parse prepared.Body
                        Assert.Equal(2, body.RootElement.GetProperty("stop_sequences").GetArrayLength())
                        let mutable stream = Unchecked.defaultof<JsonElement>
                        Assert.Equal(streaming, body.RootElement.TryGetProperty("stream", &stream))
                        if streaming then Assert.True(stream.GetBoolean())
                        let mutable thinkingConfig = Unchecked.defaultof<JsonElement>
                        Assert.Equal(thinking, body.RootElement.TryGetProperty("thinking", &thinkingConfig))
                        if thinking then Assert.Equal("adaptive", thinkingConfig.GetProperty("type").GetString())
                        if backend.GetType() = typeof<VertexAnthropicBackend> then
                            Assert.Equal("vertex-2023-10-16", body.RootElement.GetProperty("anthropic_version").GetString())
        }
