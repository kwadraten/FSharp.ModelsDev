namespace FSharp.ModelsDev.Tests

open System
open System.Collections.Generic
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.ModelsDev
open FSharp.ModelsDev.Providers
open FSharp.ModelsDev.MEAi
open FSharp.ModelsDev.Tests.Recording

module internal RecordedScenario =
    let text (node: JsonNode) (name: string) = node[name].GetValue<string>()
    let schema = """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"],"additionalProperties":false}"""
    let makeRequest (scenario: JsonNode) messages =
        let request = ChatRequest(text scenario "model", messages)
        request.MaxTokens <- Nullable(scenario["maxTokens"].GetValue<int>())
        if not (isNull scenario["temperature"]) then request.Temperature <- Nullable(scenario["temperature"].GetValue<float>())
        if not (isNull scenario["reasoningEffort"]) then request.ReasoningEffort <- text scenario "reasoningEffort"
        if text scenario "protocol" <> "gemini" && (isNull scenario["tools"] || scenario["tools"].GetValue<bool>()) then
            request.Tools <- [| ToolDefinition("get_weather", "Get current weather for a city.", schema) |]
        request
    let backendWithCredentials (scenario: JsonNode) (firstUrl: string) (credentials: ICredentialProvider) =
        let model = text scenario "model"
        let protocol = text scenario "protocol"
        let options = ProviderOptions()
        let suffix =
            match protocol with "openai-chat" -> "/chat/completions" | "openai-responses" -> "/responses" | _ -> "/messages"
        if protocol <> "gemini" then options.BaseUrl <- firstUrl.Substring(0, firstUrl.Length - suffix.Length)
        match protocol with
        | "openai-chat" -> OpenAIChatBackend(model, "recording", credentials, options) :> IChatBackend
        | "openai-responses" -> OpenAIResponsesBackend(model, "recording", credentials, options) :> IChatBackend
        | "anthropic-messages" -> AnthropicBackend(model, "recording", credentials, options) :> IChatBackend
        | "gemini" -> GeminiBackend(model, "recording", credentials, options) :> IChatBackend
        | _ -> failwith "Unknown recorded protocol"

    let backend scenario firstUrl =
        let credentials =
            { new ICredentialProvider with
                member _.GetHeaders(_, ct) =
                    ct.ThrowIfCancellationRequested()
                    let headers = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    headers["Authorization"] <- "Bearer fixture-only"
                    headers["x-api-key"] <- "fixture-only"
                    headers["x-goog-api-key"] <- "fixture-only"
                    Task.FromResult(headers :> IReadOnlyDictionary<string, string>) }
        backendWithCredentials scenario firstUrl credentials

    let stream (client: IChatModel) (request: ChatRequest) = task {
        use iterator = client.Stream(request, CancellationToken.None).GetAsyncEnumerator()
        let text = StringBuilder()
        let events = ResizeArray<ChatStreamEvent>()
        let tools = Dictionary<int, string * string * StringBuilder>()
        let mutable state: string = null
        let mutable more = true
        while more do
            let! next = iterator.MoveNextAsync().AsTask()
            more <- next
            if next then
                let event = iterator.Current
                events.Add event
                match event with
                | :? TextDelta as delta -> text.Append delta.Text |> ignore
                | :? ProviderStateUpdate as delta -> state <- delta.StateJson
                | :? ToolCallDelta as delta ->
                    let id, name, arguments =
                        match tools.TryGetValue delta.Index with
                        | true, value -> value
                        | _ -> "", "", StringBuilder()
                    let id = if String.IsNullOrEmpty delta.Id then id else delta.Id
                    let name = if String.IsNullOrEmpty delta.Name then name else delta.Name
                    arguments.Append delta.ArgumentsDelta |> ignore
                    tools[delta.Index] <- id, name, arguments
                | _ -> ()
        let terminal = Assert.Single(events |> Seq.choose (function :? Completed as doneEvent -> Some doneEvent | _ -> None))
        let calls =
            tools |> Seq.sortBy (fun pair -> pair.Key)
            |> Seq.map (fun pair -> let id, name, args = pair.Value in ToolCall(id, name, args.ToString())) |> Seq.toArray
        let assistant = ChatMessage(Role.Assistant, [| TextPart(text.ToString()) |], null, calls, null)
        assistant.ProviderStateJson <- state
        return assistant, terminal
    }

type RecordedBackendTests() =
    [<Theory; Trait("Category", "Recorded")>]
    [<InlineData("chat-tool-call")>]
    [<InlineData("responses-tool-loop")>]
    [<InlineData("messages-tool-loop")>]
    [<InlineData("gemini-cache")>]
    member _.``OpenCode HTTP cassettes exercise real serializers transport and stream parsers``(name: string) = task {
        let path = RecordingFiles.fixture("opencode/" + name)
        let cassette = CassetteJson.load path
        let scenario = cassette.Metadata["scenario"]
        use handler = new RecordedHttpHandler(path)
        use http = new HttpClient(handler)
        let backend = RecordedScenario.backend scenario cassette.Interactions[0].Request.Url
        let client = ChatClient(RecordedScenario.text scenario "model", backend, HttpChatTransport(http)) :> IChatModel
        let initial = [| ChatMessage.System(RecordedScenario.text scenario "system"); ChatMessage.User(RecordedScenario.text scenario "prompt") |]
        let! first, terminal = RecordedScenario.stream client (RecordedScenario.makeRequest scenario initial)
        if name = "gemini-cache" then
            Assert.Equal("Hi.", first.Text)
            Assert.Equal(1202L, terminal.Usage.TotalTokens)
            let! second, secondTerminal = RecordedScenario.stream client (RecordedScenario.makeRequest scenario initial)
            Assert.Equal("Hi.", second.Text)
            Assert.Equal(1202L, secondTerminal.Usage.TotalTokens)
        else
            Assert.Equal(FinishReason.ToolCalls, terminal.FinishReason)
            let call = Assert.Single first.ToolCalls
            Assert.Equal("get_weather", call.Name)
            Assert.False(String.IsNullOrEmpty call.Id)
            use args = JsonDocument.Parse call.ArgumentsJson
            Assert.Equal("Paris", args.RootElement.GetProperty("city").GetString())
            if cassette.Interactions.Count > 1 then
                Assert.False(String.IsNullOrEmpty first.ProviderStateJson)
                // Exercise the MEAI message boundary as well as native state replay.
                let roundTripped = first |> Conversions.messageToAi |> Conversions.messageFromAi
                let result = ChatMessage.ToolResult(call.Id, RecordedScenario.text scenario "toolResult")
                let messages = Array.append initial [| roundTripped; result |]
                let! final, finalTerminal = RecordedScenario.stream client (RecordedScenario.makeRequest scenario messages)
                Assert.Equal("Paris is sunny.", final.Text)
                Assert.Equal(FinishReason.Stop, finalTerminal.FinishReason)
                Assert.True(finalTerminal.Usage.InputTokens > 0L)
                Assert.True(finalTerminal.Usage.OutputTokens > 0L)
        handler.VerifyComplete()
    }

    [<Theory; Trait("Category", "Recorded")>]
    [<InlineData("chat-tool-call")>]
    [<InlineData("responses-tool-loop")>]
    [<InlineData("messages-tool-loop")>]
    [<InlineData("gemini-cache")>]
    member _.``imported response bodies retain upstream SHA256 provenance``(name: string) =
        let cassette = CassetteJson.load (RecordingFiles.fixture("opencode/" + name))
        let source = cassette.Metadata["source"]
        Assert.Equal("81200db2a3a86fc72a60dff996557f59865a6f24", RecordedScenario.text source "commit")
        Assert.Equal("MIT", RecordedScenario.text source "license")
        Assert.Equal(cassette.Interactions.Count, source["responseBodySha256"].AsArray().Count)
        for index in 0 .. cassette.Interactions.Count - 1 do
            let digest = SHA256.HashData(Encoding.UTF8.GetBytes(cassette.Interactions[index].Response.Body))
            Assert.Equal(source.["responseBodySha256"].[index].GetValue<string>(), Convert.ToHexString(digest).ToLowerInvariant())
