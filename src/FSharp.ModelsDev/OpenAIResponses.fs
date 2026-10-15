namespace FSharp.ModelsDev

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// OpenAI's stateless Responses API backend. Reasoning items with encrypted_content
/// are carried in ChatMessage.ProviderStateJson so callers can replay full history.
[<Sealed>]
type OpenAIResponsesBackend(model: string, accountId: string, credentials: ICredentialProvider, options: ProviderOptions) as this =
    let options = if isNull options then ProviderOptions() else options
    let credentials = if isNull credentials then options.Credentials else credentials
    let codexProtocol = String.Equals(options.ProtocolId, "codex-responses", StringComparison.OrdinalIgnoreCase)
    let baseUrl =
        if String.IsNullOrWhiteSpace options.BaseUrl then
            if codexProtocol then "https://chatgpt.com/backend-api/codex" else "https://api.openai.com/v1"
        else options.BaseUrl.TrimEnd('/')
    let url = baseUrl + "/responses"
    let backendId = "openai-responses"
    let isCodexCompletion =
        codexProtocol
        || (try
                let endpoint = Uri(baseUrl, UriKind.Absolute)
                endpoint.Scheme = Uri.UriSchemeHttps
                && endpoint.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase)
                && (endpoint.AbsolutePath.TrimEnd('/') = "/backend-api/codex")
            with _ -> false)

    let fail message = raise (AiException(AiError.Decode($"Unsupported: {message}")))

    let tryProperty (element: JsonElement) (name: string) =
        if element.ValueKind <> JsonValueKind.Object then None
        else
            match element.TryGetProperty name with
            | true, value when value.ValueKind <> JsonValueKind.Null && value.ValueKind <> JsonValueKind.Undefined -> Some value
            | _ -> None

    let getString (element: JsonElement) (name: string) (fallback: string) =
        match tryProperty element name with
        | Some value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ -> fallback

    let writeRawJson (writer: Utf8JsonWriter) (json: string) =
        try
            use document = JsonDocument.Parse json
            document.RootElement.WriteTo writer
        with :? JsonException as ex -> raise (AiException(AiError.Decode "Invalid JSON in provider state or tool schema.", ex))

    let messageText (message: ChatMessage) = if isNull message.Parts then "" else message.Text

    let stateItems (currentModel: string) (json: string) =
        if String.IsNullOrWhiteSpace json then Array.empty<JsonElement>
        else
            try
                use document = JsonDocument.Parse json
                let root = document.RootElement
                let stateModel = getString root "model" ""
                let modelMatches = stateModel = currentModel
                if root.ValueKind <> JsonValueKind.Object
                   || getString root "provider" "" <> backendId
                   || not modelMatches then
                    raise (AiException(AiError.Decode "Responses provider state belongs to a different provider or model."))
                match tryProperty root "items" with
                | Some items when items.ValueKind = JsonValueKind.Array ->
                    items.EnumerateArray()
                    |> Seq.map (fun item ->
                        match getString item "type" "" with
                        | "reasoning" ->
                            match tryProperty item "encrypted_content" with
                            | Some encrypted when encrypted.ValueKind = JsonValueKind.String -> item.Clone()
                            | _ -> raise (AiException(AiError.Decode "Responses provider state contains reasoning without encrypted_content."))
                        | "message" | "function_call" -> item.Clone()
                        | _ -> raise (AiException(AiError.Decode "Responses provider state contains an unsupported item.")))
                    |> Seq.toArray
                | _ -> raise (AiException(AiError.Decode "Responses provider state has no items array."))
            with
            | :? AiException -> reraise()
            | :? JsonException as ex -> raise (AiException(AiError.Decode "Invalid Responses provider state JSON.", ex))

    let writeState (currentModel: string) (writer: Utf8JsonWriter) (json: string) =
        for item in stateItems currentModel json do item.WriteTo writer

    let writeMessageContent (writer: Utf8JsonWriter) (message: ChatMessage) =
        let hasImages = not (isNull message.Parts) && (message.Parts |> Seq.exists (function :? ImagePart -> true | _ -> false))
        if hasImages then
            writer.WriteStartArray()
            if not (isNull message.Parts) then
                for part in message.Parts do
                    match part with
                    | :? TextPart as text ->
                        writer.WriteStartObject(); writer.WriteString("type", "input_text"); writer.WriteString("text", text.Text); writer.WriteEndObject()
                    | :? ImagePart as image ->
                        if String.IsNullOrWhiteSpace image.Url then fail "Responses image inputs require a URL or data URL."
                        writer.WriteStartObject(); writer.WriteString("type", "input_image"); writer.WriteString("image_url", image.Url); writer.WriteEndObject()
                    | part -> fail $"Content part '{part.Kind}' is not supported by OpenAI Responses."
            writer.WriteEndArray()
        else
            writer.WriteStringValue(message.Text)

    let serializeRequest (request: ChatRequest) (stream: bool) =
        if not (isNull request.StopSequences) && request.StopSequences.Count > 0 then
            fail "OpenAI Responses does not support ChatRequest.StopSequences."
        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteString("model", (if String.IsNullOrEmpty request.Model then model else request.Model))
        writer.WriteBoolean("store", false)
        writer.WriteBoolean("stream", stream)
        writer.WritePropertyName("include")
        writer.WriteStartArray()
        writer.WriteStringValue("reasoning.encrypted_content")
        writer.WriteEndArray()
        if request.Temperature.HasValue then writer.WriteNumber("temperature", request.Temperature.Value)
        if request.TopP.HasValue then writer.WriteNumber("top_p", request.TopP.Value)
        if request.MaxTokens.HasValue then writer.WriteNumber("max_output_tokens", request.MaxTokens.Value)
        if request.ReasoningBudgetTokens.HasValue then
            fail "OpenAI Responses does not expose a reasoning token budget; use ReasoningEffort."
        if not (String.IsNullOrWhiteSpace request.ReasoningEffort) then
            writer.WritePropertyName("reasoning"); writer.WriteStartObject(); writer.WriteString("effort", request.ReasoningEffort); writer.WriteEndObject()
        writer.WritePropertyName("input")
        writer.WriteStartArray()
        if not (isNull request.Messages) then
            for message in request.Messages do
                if isNull message then fail "Chat request contains a null message."
                if not (String.IsNullOrWhiteSpace message.Name) then fail "Named chat messages are not supported by OpenAI Responses."
                match message.Role with
                | Role.System ->
                    if not (isNull message.Parts) && (message.Parts |> Seq.exists (function :? ImagePart -> true | _ -> false)) then
                        fail "OpenAI Responses image inputs are supported only on user messages."
                    if not (isNull message.ToolCalls) && message.ToolCalls.Count > 0 then fail "System messages cannot contain tool calls."
                    writer.WriteStartObject(); writer.WriteString("role", "developer"); writer.WritePropertyName("content"); writeMessageContent writer message; writer.WriteEndObject()
                | Role.User ->
                    if not (isNull message.ToolCalls) && message.ToolCalls.Count > 0 then fail "User messages cannot contain tool calls."
                    writer.WriteStartObject(); writer.WriteString("role", "user"); writer.WritePropertyName("content"); writeMessageContent writer message; writer.WriteEndObject()
                | Role.Assistant ->
                    if not (String.IsNullOrWhiteSpace message.ProviderStateJson) then
                        writeState (if String.IsNullOrEmpty request.Model then model else request.Model) writer message.ProviderStateJson
                    else
                        if not (String.IsNullOrWhiteSpace (messageText message)) then
                            writer.WriteStartObject(); writer.WriteString("role", "assistant"); writer.WritePropertyName("content"); writer.WriteStartArray()
                            writer.WriteStartObject(); writer.WriteString("type", "output_text"); writer.WriteString("text", messageText message); writer.WriteEndObject()
                            writer.WriteEndArray(); writer.WriteEndObject()
                        if not (isNull message.ToolCalls) && message.ToolCalls.Count > 0 then
                            for call in message.ToolCalls do
                                writer.WriteStartObject(); writer.WriteString("type", "function_call"); writer.WriteString("call_id", call.Id); writer.WriteString("name", call.Name)
                                writer.WritePropertyName("arguments"); writer.WriteStringValue(if isNull call.ArgumentsJson then "{}" else call.ArgumentsJson); writer.WriteEndObject()
                        elif String.IsNullOrWhiteSpace (messageText message) then
                            fail "Empty assistant messages are not supported by OpenAI Responses."
                | Role.Tool ->
                    if String.IsNullOrWhiteSpace message.ToolCallId then fail "Tool result messages require ToolCallId for Responses function_call_output."
                    writer.WriteStartObject(); writer.WriteString("type", "function_call_output"); writer.WriteString("call_id", message.ToolCallId); writer.WriteString("output", messageText message); writer.WriteEndObject()
                | _ -> fail "Unsupported chat message role for OpenAI Responses."
        writer.WriteEndArray()
        if not (isNull request.Tools) && request.Tools.Count > 0 then
            writer.WritePropertyName("tools"); writer.WriteStartArray()
            for tool in request.Tools do
                writer.WriteStartObject(); writer.WriteString("type", "function"); writer.WriteString("name", tool.Name)
                if not (isNull tool.Description) then writer.WriteString("description", tool.Description)
                writer.WritePropertyName("parameters")
                if String.IsNullOrWhiteSpace tool.ParametersJson then writer.WriteStartObject(); writer.WriteEndObject()
                else writeRawJson writer tool.ParametersJson
                writer.WriteEndObject()
            writer.WriteEndArray()
        writer.WriteEndObject(); writer.Flush()
        Encoding.UTF8.GetString(buffer.ToArray())

    let mapUsage (root: JsonElement) =
        match tryProperty root "usage" with
        | Some usage when usage.ValueKind = JsonValueKind.Object ->
            let getInt name =
                match tryProperty usage name with
                | Some value when value.ValueKind = JsonValueKind.Number -> match value.TryGetInt64() with true, n -> n | _ -> 0L
                | _ -> 0L
            Usage(getInt "input_tokens", getInt "output_tokens", getInt "total_tokens")
        | _ -> Usage()

    let mapFinish (status: string) (incompleteReason: string) =
        match status with
        | "completed" -> FinishReason.Stop
        | "incomplete" when incompleteReason = "content_filter" -> FinishReason.ContentFilter
        | "incomplete" -> FinishReason.Length
        | "failed" -> raise (AiException(AiError.Decode "OpenAI Responses returned a failed response."))
        | _ -> FinishReason.Unknown

    let parseResponse (body: string) =
        try
            use document = JsonDocument.Parse body
            let root = document.RootElement
            let status = getString root "status" ""
            let incompleteReason =
                match tryProperty root "incomplete_details" with
                | Some details -> getString details "reason" ""
                | _ -> ""
            if status = "failed" then
                let err = match tryProperty root "error" with Some value -> getString value "message" "OpenAI Responses failed." | _ -> "OpenAI Responses failed."
                raise (AiException(AiError.Decode err))
            let text = StringBuilder()
            let calls = ResizeArray<ToolCall>()
            let providerItems = ResizeArray<string>()
            match tryProperty root "output" with
            | Some items when items.ValueKind = JsonValueKind.Array ->
                for item in items.EnumerateArray() do
                    match getString item "type" "" with
                    | "message" ->
                        providerItems.Add(item.GetRawText())
                        match tryProperty item "content" with
                        | Some content when content.ValueKind = JsonValueKind.Array ->
                            for part in content.EnumerateArray() do
                                match getString part "type" "" with
                                | "output_text" -> text.Append(getString part "text" "") |> ignore
                                | "refusal" -> text.Append(getString part "refusal" "") |> ignore
                                | other -> fail $"OpenAI Responses output content '{other}' is not supported."
                        | _ -> ()
                    | "function_call" ->
                        providerItems.Add(item.GetRawText())
                        calls.Add(ToolCall(getString item "call_id" "", getString item "name" "", getString item "arguments" "{}"))
                    | "reasoning" -> providerItems.Add(item.GetRawText())
                    | "web_search_call" | "file_search_call" | "computer_call" | "code_interpreter_call" as kind -> fail $"OpenAI Responses output item '{kind}' is not supported by this chat contract."
                    | kind -> fail $"Unknown OpenAI Responses output item '{kind}'."
            | _ -> ()
            let message = ChatMessage(Role.Assistant, [| TextPart(text.ToString()) |], null, (if calls.Count = 0 then null else calls :> IReadOnlyList<ToolCall>), null)
            if providerItems.Count > 0 then
                let state = JsonSerializer.Serialize {| provider = backendId; model = model; items = providerItems |> Seq.map (fun raw -> JsonDocument.Parse(raw).RootElement.Clone()) |> Seq.toArray |}
                message.ProviderStateJson <- state
            let reason =
                if calls.Count > 0 && status = "completed" then FinishReason.ToolCalls
                else mapFinish status incompleteReason
            let usage = mapUsage root
            ChatResponse(getString root "id" "", getString root "model" model, message, usage, reason)
        with
        | :? AiException -> reraise()
        | :? JsonException as ex -> raise (AiException(AiError.Decode $"Failed to parse OpenAI Responses body: {ex.Message}", ex))

    let createStreamParser () =
        let toolItems = Dictionary<int, struct (string * string)>()
        let argumentLengths = Dictionary<int, int>()
        let providerItems = SortedDictionary<int, string>()
        let mutable responseId = ""
        let mutable responseModel = model
        let mutable finalUsage: Usage = null
        let mutable completed = false
        let mutable terminalResponseJson: string = null

        let stateUpdate () =
            if providerItems.Count = 0 then Array.empty<ChatStreamEvent> :> IReadOnlyList<ChatStreamEvent>
            else
                let json = JsonSerializer.Serialize {| provider = backendId; model = model; items = providerItems.Values |> Seq.map (fun raw -> JsonDocument.Parse(raw).RootElement.Clone()) |> Seq.toArray |}
                [| ProviderStateUpdate(json) :> ChatStreamEvent |] :> IReadOnlyList<ChatStreamEvent>

        let parseEvent (eventName: string) (data: string) =
            try
                use document = JsonDocument.Parse data
                let root = document.RootElement
                let actualType = getString root "type" (if isNull eventName then "" else eventName)
                match actualType with
                | "response.created" | "response.in_progress" | "response.queued" ->
                    match tryProperty root "response" with
                    | Some response -> responseId <- getString response "id" responseId; responseModel <- getString response "model" responseModel
                    | _ -> ()
                    Array.empty<ChatStreamEvent> :> IReadOnlyList<ChatStreamEvent>
                | "response.output_item.added" ->
                    match tryProperty root "item" with
                    | Some item when getString item "type" "" = "function_call" ->
                        let index = match tryProperty root "output_index" with Some v when v.ValueKind = JsonValueKind.Number -> v.GetInt32() | _ -> 0
                        toolItems[index] <- struct (getString item "call_id" "", getString item "name" "")
                    | Some item when getString item "type" "" = "reasoning" -> ()
                    | Some item when getString item "type" "" = "message" -> ()
                    | Some item ->
                        let itemType = getString item "type" ""
                        fail $"OpenAI Responses output item '{itemType}' is not supported."
                    | _ -> ()
                    Array.empty<ChatStreamEvent> :> IReadOnlyList<ChatStreamEvent>
                | "response.output_text.delta" -> [| TextDelta(getString root "delta" "") :> ChatStreamEvent |] :> IReadOnlyList<ChatStreamEvent>
                | "response.refusal.delta" -> [| TextDelta(getString root "delta" "") :> ChatStreamEvent |] :> IReadOnlyList<ChatStreamEvent>
                | "response.function_call_arguments.delta" ->
                    let index = match tryProperty root "output_index" with Some v when v.ValueKind = JsonValueKind.Number -> v.GetInt32() | _ -> 0
                    let id, name = match toolItems.TryGetValue index with true, struct (id, name) -> id, name | _ -> getString root "call_id" "", getString root "name" ""
                    let delta = getString root "delta" ""
                    argumentLengths[index] <- (match argumentLengths.TryGetValue index with true, length -> length + delta.Length | _ -> delta.Length)
                    [| ToolCallDelta(index, id, name, delta) :> ChatStreamEvent |] :> IReadOnlyList<ChatStreamEvent>
                | "response.function_call_arguments.done" ->
                    let index = match tryProperty root "output_index" with Some v when v.ValueKind = JsonValueKind.Number -> v.GetInt32() | _ -> 0
                    let arguments = getString root "arguments" ""
                    let already = match argumentLengths.TryGetValue index with true, length -> length | _ -> 0
                    if arguments.Length > already then
                        let id, name = match toolItems.TryGetValue index with true, struct (id, name) -> id, name | _ -> getString root "call_id" "", getString root "name" ""
                        argumentLengths[index] <- arguments.Length
                        [| ToolCallDelta(index, id, name, arguments.Substring(already)) :> ChatStreamEvent |] :> IReadOnlyList<ChatStreamEvent>
                    else Array.empty<ChatStreamEvent> :> IReadOnlyList<ChatStreamEvent>
                | "response.output_item.done" ->
                    match tryProperty root "item" with
                    | Some item when getString item "type" "" = "reasoning" ->
                        let outputIndex = match tryProperty root "output_index" with Some v when v.ValueKind = JsonValueKind.Number -> v.GetInt32() | _ -> providerItems.Count
                        providerItems[outputIndex] <- item.GetRawText()
                        stateUpdate ()
                    | Some item when getString item "type" "" = "function_call" -> Array.empty<ChatStreamEvent> :> IReadOnlyList<ChatStreamEvent>
                    | Some item when getString item "type" "" = "message" -> Array.empty<ChatStreamEvent> :> IReadOnlyList<ChatStreamEvent>
                    | Some item ->
                        let itemType = getString item "type" ""
                        fail $"OpenAI Responses output item '{itemType}' is not supported."
                    | _ -> Array.empty<ChatStreamEvent> :> IReadOnlyList<ChatStreamEvent>
                | ("response.completed" | "response.incomplete") when completed -> Array.empty<ChatStreamEvent> :> IReadOnlyList<ChatStreamEvent>
                | "response.completed" | "response.incomplete" ->
                    let events = ResizeArray<ChatStreamEvent>()
                    match tryProperty root "response" with
                    | Some response ->
                        terminalResponseJson <- response.GetRawText()
                        responseId <- getString response "id" responseId
                        responseModel <- getString response "model" responseModel
                        finalUsage <- mapUsage response
                        match tryProperty response "output" with
                        | Some output when output.ValueKind = JsonValueKind.Array ->
                            let mutable outputIndex = 0
                            for item in output.EnumerateArray() do
                                if getString item "type" "" = "reasoning" then
                                    match tryProperty item "encrypted_content" with
                                    | Some encrypted when encrypted.ValueKind = JsonValueKind.String -> providerItems[outputIndex] <- item.GetRawText()
                                    | _ -> ()
                                elif getString item "type" "" = "function_call" then
                                    let id, name = getString item "call_id" "", getString item "name" ""
                                    toolItems[outputIndex] <- struct (id, name)
                                    providerItems[outputIndex] <- item.GetRawText()
                                    let arguments = getString item "arguments" ""
                                    let already = match argumentLengths.TryGetValue outputIndex with true, length -> length | _ -> 0
                                    if arguments.Length > already then
                                        events.Add(ToolCallDelta(outputIndex, id, name, arguments.Substring(already)))
                                        argumentLengths[outputIndex] <- arguments.Length
                                elif getString item "type" "" = "message" then
                                    providerItems[outputIndex] <- item.GetRawText()
                                else
                                    let itemType = getString item "type" ""
                                    fail $"OpenAI Responses output item '{itemType}' is not supported."
                                outputIndex <- outputIndex + 1
                        | _ -> ()
                    | _ -> ()
                    completed <- true
                    let reason =
                        if actualType = "response.completed" then FinishReason.Stop
                        else
                            let incompleteReason =
                                match tryProperty root "response" with
                                | Some response ->
                                    match tryProperty response "incomplete_details" with
                                    | Some details -> getString details "reason" ""
                                    | _ -> ""
                                | _ -> ""
                            mapFinish "incomplete" incompleteReason
                    let reason = if actualType = "response.completed" && toolItems.Count > 0 then FinishReason.ToolCalls else reason
                    if providerItems.Count > 0 then events.AddRange(stateUpdate())
                    events.Add(Completed(reason, (if isNull finalUsage then Usage() else finalUsage)))
                    events.ToArray() :> IReadOnlyList<ChatStreamEvent>
                | "response.failed" | "error" ->
                    let errorText =
                        match tryProperty root "response" with
                        | Some response ->
                            match tryProperty response "error" with
                            | Some error -> getString error "message" "OpenAI Responses failed."
                            | _ -> "OpenAI Responses failed."
                        | _ ->
                            match tryProperty root "error" with
                            | Some error -> getString error "message" (getString root "message" "OpenAI Responses failed.")
                            | _ -> getString root "message" "OpenAI Responses failed."
                    raise (AiException(AiError.Decode errorText))
                | "response.reasoning_summary_text.delta" | "response.reasoning_summary_text.done" -> Array.empty<ChatStreamEvent> :> IReadOnlyList<ChatStreamEvent>
                | "response.content_part.added" | "response.content_part.done" | "response.output_text.done" | "response.refusal.done" -> Array.empty<ChatStreamEvent> :> IReadOnlyList<ChatStreamEvent>
                | other ->
                    if String.IsNullOrEmpty other then Array.empty<ChatStreamEvent> :> IReadOnlyList<ChatStreamEvent>
                    else fail $"OpenAI Responses stream event '{other}' is not supported."
            with
            | :? AiException -> reraise()
            | :? JsonException as ex -> raise (AiException(AiError.Decode $"Failed to parse OpenAI Responses event: {ex.Message}", ex))

        let frameParser = SseFrameParser(parseEvent)
        { new IChatStreamParser with
            member _.PushLine(line: string) = frameParser.PushLine line
            member _.Finish() =
                let events = ResizeArray<ChatStreamEvent>()
                events.AddRange(frameParser.Finish())
                if not completed then raise (AiException(AiError.Decode "OpenAI Responses stream ended before a terminal response event."))
                events.ToArray() :> IReadOnlyList<ChatStreamEvent>
          interface IChatCompletionStreamParser with
            member _.GetResponse() =
                if not completed || isNull terminalResponseJson then
                    raise (AiException(AiError.Decode "OpenAI Responses stream has no completed response."))
                parseResponse terminalResponseJson }

    interface IChatBackend with
        member _.PrepareRequest(request, stream, cancellationToken) =
            task {
                if isNull credentials then raise (AiException(AiError.Transport "OpenAI Responses requires a credential provider."))
                let! authHeaders = credentials.GetHeaders(accountId, cancellationToken)
                let headers = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                if not (isNull options.Headers) then
                    for KeyValue(key, value) in options.Headers do headers[key] <- value
                if not (isNull authHeaders) then
                    for KeyValue(key, value) in authHeaders do headers[key] <- value
                headers["Content-Type"] <- "application/json"
                return PreparedChatRequest(url, headers :> IReadOnlyDictionary<string, string>, serializeRequest request stream)
            }
        member _.ParseResponse(body) = parseResponse body
        member _.CreateStreamParser() = createStreamParser ()

    interface IStreamingCompletionBackend with
        member _.UseStreamingForCompletions = isCodexCompletion

    interface IRequestScopedBackend with
        member _.ResolveBackend(requestModel) =
            if String.IsNullOrEmpty requestModel || requestModel = model then this :> IChatBackend
            else OpenAIResponsesBackend(requestModel, accountId, credentials, options) :> IChatBackend
