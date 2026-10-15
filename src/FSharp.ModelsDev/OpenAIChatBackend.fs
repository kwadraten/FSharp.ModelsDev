namespace FSharp.ModelsDev.Providers

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes
open System.Text
open FSharp.ModelsDev

/// OpenAI Chat Completions with request-time credentials and one parser per stream.
[<Sealed>]
type OpenAIChatBackend(model: string, accountId: string, credentials: ICredentialProvider, options: ProviderOptions) =
    let protocol = OpenAICompatibleChatProtocol() :> IChatProtocol
    let baseUrl =
        if isNull options || String.IsNullOrWhiteSpace options.BaseUrl then "https://api.openai.com/v1"
        else options.BaseUrl.TrimEnd('/')

    let stateJson (reasoning: string) =
        let state = JsonObject()
        state["provider"] <- JsonValue.Create("openai-chat")
        state["model"] <- JsonValue.Create(model)
        state["reasoning_content"] <- JsonValue.Create(reasoning)
        state.ToJsonString()

    let serialize request stream =
        let body = JsonNode.Parse(protocol.SerializeRequest(request, stream))
        if not (isNull request.Messages) then
            let messages = body["messages"].AsArray()
            for index in 0 .. request.Messages.Count - 1 do
                let message = request.Messages[index]
                if message.Role = Role.Assistant && not (String.IsNullOrWhiteSpace message.ProviderStateJson) then
                    try
                        use state = JsonDocument.Parse(message.ProviderStateJson)
                        let root = state.RootElement
                        let get name = match root.TryGetProperty(name: string) with true, p when p.ValueKind = JsonValueKind.String -> p.GetString() | _ -> null
                        if get "provider" = "openai-chat" && get "model" = request.Model then
                            let reasoning = get "reasoning_content"
                            if not (isNull reasoning) then messages[index]["reasoning_content"] <- JsonValue.Create(reasoning)
                    with :? JsonException as ex -> raise (AiException(AiError.Decode "Invalid chat reasoning provider state.", ex))
        body.ToJsonString()

    let createParser () =
        let mutable usage = Usage()
        let mutable finish = FinishReason.Unknown
        let mutable finished = false
        let mutable completed = false
        let reasoning = StringBuilder()
        let text name (element: JsonElement) =
            match element.TryGetProperty(name: string) with
            | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
            | _ -> null
        let integer name (element: JsonElement) =
            match element.TryGetProperty(name: string) with
            | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt64()
            | _ -> 0L
        let onFrame _ (data: string) =
            let events = ResizeArray<ChatStreamEvent>()
            if data = "[DONE]" then finished <- true
            elif not (String.IsNullOrWhiteSpace data) then
                try
                    use document = JsonDocument.Parse(data)
                    let root = document.RootElement
                    if root.TryGetProperty("error") |> fst then
                        raise (AiException(AiError.Decode $"OpenAI stream error: {data}"))
                    match root.TryGetProperty("usage") with
                    | true, value when value.ValueKind = JsonValueKind.Object ->
                        usage <- Usage(integer "prompt_tokens" value, integer "completion_tokens" value, integer "total_tokens" value)
                        events.Add(UsageUpdate usage)
                    | _ -> ()
                    match root.TryGetProperty("choices") with
                    | true, choices when choices.ValueKind = JsonValueKind.Array ->
                        for choice in choices.EnumerateArray() do
                            let index = integer "index" choice
                            if index = 0L then
                                match text "finish_reason" choice with
                                | null -> ()
                                | reason ->
                                    finished <- true
                                    finish <-
                                        match reason with
                                        | "stop" -> FinishReason.Stop
                                        | "length" -> FinishReason.Length
                                        | "tool_calls" | "function_call" -> FinishReason.ToolCalls
                                        | "content_filter" -> FinishReason.ContentFilter
                                        | _ -> FinishReason.Unknown
                                match choice.TryGetProperty("delta") with
                                | true, delta when delta.ValueKind = JsonValueKind.Object ->
                                    match text "reasoning_content" delta with
                                    | null | "" -> ()
                                    | value ->
                                        reasoning.Append(value) |> ignore
                                        events.Add(ProviderStateUpdate(stateJson (reasoning.ToString())))
                                    match text "content" delta with
                                    | null | "" -> ()
                                    | value -> events.Add(TextDelta value)
                                    match text "refusal" delta with
                                    | null | "" -> ()
                                    | value -> events.Add(TextDelta value)
                                    match delta.TryGetProperty("tool_calls") with
                                    | true, calls when calls.ValueKind = JsonValueKind.Array ->
                                        for call in calls.EnumerateArray() do
                                            let mutable name: string = null
                                            let mutable arguments: string = null
                                            match call.TryGetProperty("function") with
                                            | true, fn when fn.ValueKind = JsonValueKind.Object ->
                                                name <- text "name" fn
                                                arguments <- text "arguments" fn
                                            | _ -> ()
                                            events.Add(ToolCallDelta(int (integer "index" call), text "id" call, name, arguments))
                                    | _ -> ()
                                | _ -> ()
                    | _ -> ()
                with :? JsonException as error ->
                    raise (AiException(AiError.Decode "Invalid OpenAI stream JSON.", error))
            events :> IReadOnlyList<ChatStreamEvent>
        let frames = SseFrameParser(onFrame)
        { new IChatStreamParser with
            member _.PushLine(line) = frames.PushLine(line)
            member _.Finish() =
                let events = ResizeArray<ChatStreamEvent>(frames.Finish())
                if not completed then
                    if not finished then raise (AiException(AiError.Decode "OpenAI stream ended before a terminal event."))
                    completed <- true
                    events.Add(Completed(finish, usage))
                events :> IReadOnlyList<ChatStreamEvent> }

    interface IChatBackend with
        member _.PrepareRequest(request, stream, ct) =
            task {
                if request.ReasoningBudgetTokens.HasValue then
                    raise (AiException("OpenAI Chat Completions accepts ReasoningEffort, not ReasoningBudgetTokens."))
                let! headers = credentials.GetHeaders(accountId, ct)
                return PreparedChatRequest(baseUrl + "/chat/completions", headers, serialize request stream)
            }
        member _.ParseResponse(body) =
            let response = protocol.ParseResponse(body)
            use document = JsonDocument.Parse(body)
            match document.RootElement.TryGetProperty("choices") with
            | true, choices when choices.ValueKind = JsonValueKind.Array && choices.GetArrayLength() > 0 ->
                match choices[0].TryGetProperty("message") with
                | true, message when message.ValueKind = JsonValueKind.Object ->
                    match message.TryGetProperty("reasoning_content") with
                    | true, value when value.ValueKind = JsonValueKind.String -> response.Message.ProviderStateJson <- stateJson (value.GetString())
                    | _ -> ()
                | _ -> ()
            | _ -> ()
            response
        member _.CreateStreamParser() = createParser ()
    interface IRequestScopedBackend with
        member _.ResolveBackend(modelId) = OpenAIChatBackend(modelId, accountId, credentials, options) :> IChatBackend
