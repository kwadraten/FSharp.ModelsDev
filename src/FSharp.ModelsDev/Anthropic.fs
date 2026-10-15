namespace FSharp.ModelsDev.Providers

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open FSharp.ModelsDev

/// Anthropic Messages payload codec used by both first-party and Vertex Claude endpoints.
[<Sealed>]
type internal AnthropicCodec(model: string, stateProtocolId: string, vertex: bool) =
    let validState (m: ChatMessage) =
        if String.IsNullOrEmpty m.ProviderStateJson then None
        else
            let parsed = try JsonDocument.Parse(m.ProviderStateJson) with :? JsonException as ex -> raise (AiException(AiError.Decode("Invalid preserved Claude provider state JSON: " + ex.Message), ex))
            use state = parsed
            let root = state.RootElement
            if ProviderJson.str root "protocol" "" <> stateProtocolId || ProviderJson.str root "model" "" <> model then None
            else
                match ProviderJson.prop root "content" with
                | Some content when content.ValueKind = JsonValueKind.Array -> Some (content.GetRawText())
                | _ -> None
    let jsonError msg (ex: exn) = AiException(AiError.Decode(msg + ex.Message), ex)
    let inputTokenCount (usage: JsonElement) =
        ProviderJson.int64 usage "input_tokens"
        + ProviderJson.int64 usage "cache_read_input_tokens"
        + ProviderJson.int64 usage "cache_creation_input_tokens"
    let providerState (blocks: ResizeArray<JsonElement>) (stateModel: string) =
        use ms = new MemoryStream()
        use w = new Utf8JsonWriter(ms)
        w.WriteStartObject()
        w.WriteString("protocol", stateProtocolId)
        w.WriteString("model", stateModel)
        w.WritePropertyName("content")
        w.WriteStartArray()
        for block in blocks do block.WriteTo(w)
        w.WriteEndArray()
        w.WriteEndObject()
        w.Flush()
        Encoding.UTF8.GetString(ms.ToArray())
    let writeImage (w: Utf8JsonWriter) (img: ImagePart) =
        let u = img.Url
        if isNull u then raise (AiException("Claude image input has no URL."))
        w.WriteStartObject(); w.WriteString("type", "image"); w.WritePropertyName("source"); w.WriteStartObject()
        if u.StartsWith("data:", StringComparison.OrdinalIgnoreCase) then
            let comma = u.IndexOf(',')
            if comma < 0 || not (u.Substring(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) then raise (AiException("Claude image data URI must contain base64-encoded bytes."))
            let mime = if String.IsNullOrWhiteSpace img.MediaType then u.Substring(5, u.IndexOf(';') - 5) else img.MediaType
            w.WriteString("type", "base64"); w.WriteString("media_type", mime); w.WriteString("data", u.Substring(comma + 1))
        elif Uri.IsWellFormedUriString(u, UriKind.Absolute) then
            w.WriteString("type", "url"); w.WriteString("url", u)
        else raise (AiException("Claude image input must be a base64 data URI or absolute image URL."))
        w.WriteEndObject(); w.WriteEndObject()

    let writeUserParts (w: Utf8JsonWriter) (m: ChatMessage) =
        w.WriteStartArray()
        if not (isNull m.Parts) then
            for p in m.Parts do
                match p with
                | :? TextPart as t -> w.WriteStartObject(); w.WriteString("type", "text"); w.WriteString("text", t.Text); w.WriteEndObject()
                | :? ImagePart as i -> writeImage w i
                | _ -> raise (AiException("Claude supports text and image message parts only."))
        w.WriteEndArray()

    member _.Serialize(request: ChatRequest) =
        use ms = new MemoryStream()
        use w = new Utf8JsonWriter(ms)
        let systems = ResizeArray<string>()
        w.WriteStartObject()
        if not vertex then w.WriteString("model", model)
        let maxTokens = if request.MaxTokens.HasValue then request.MaxTokens.Value else 4096
        if maxTokens < 1 then raise (AiException("Claude max_tokens must be positive."))
        w.WriteNumber("max_tokens", maxTokens)
        w.WritePropertyName("messages"); w.WriteStartArray()
        if not (isNull request.Messages) then
            let mutable messageIndex = 0
            while messageIndex < request.Messages.Count do
                let m = request.Messages[messageIndex]
                if m.Role = Role.System then
                    if not (isNull m.Parts) then for p in m.Parts do match p with :? TextPart as t -> systems.Add(t.Text) | _ -> raise (AiException("Claude system instruction only accepts text."))
                    messageIndex <- messageIndex + 1
                elif m.Role = Role.Tool then
                    // Anthropic requires all results for a parallel tool turn in the next user message.
                    w.WriteStartObject()
                    w.WriteString("role", "user")
                    w.WritePropertyName("content")
                    w.WriteStartArray()
                    while messageIndex < request.Messages.Count && request.Messages[messageIndex].Role = Role.Tool do
                        let result = request.Messages[messageIndex]
                        w.WriteStartObject()
                        w.WriteString("type", "tool_result")
                        w.WriteString("tool_use_id", result.ToolCallId)
                        w.WriteString("content", result.Text)
                        w.WriteEndObject()
                        messageIndex <- messageIndex + 1
                    w.WriteEndArray()
                    w.WriteEndObject()
                else
                    w.WriteStartObject(); w.WriteString("role", if m.Role = Role.User || m.Role = Role.Tool then "user" else "assistant"); w.WritePropertyName("content")
                    match validState m with
                    | Some raw when m.Role = Role.Assistant ->
                        try
                            use d = JsonDocument.Parse(raw)
                            d.RootElement.WriteTo(w)
                        with :? JsonException as ex -> raise (jsonError "Invalid preserved Claude provider state: " ex)
                    | _ when m.Role = Role.Assistant && not (isNull m.ToolCalls) && m.ToolCalls.Count > 0 ->
                        w.WriteStartArray()
                        if not (isNull m.Parts) then
                            for p in m.Parts do
                                match p with
                                | :? TextPart as t ->
                                    w.WriteStartObject()
                                    w.WriteString("type", "text")
                                    w.WriteString("text", t.Text)
                                    w.WriteEndObject()
                                | _ -> raise (AiException("Claude assistant supports text content parts."))
                        for c in m.ToolCalls do
                            w.WriteStartObject()
                            w.WriteString("type", "tool_use")
                            w.WriteString("id", c.Id)
                            w.WriteString("name", c.Name)
                            w.WritePropertyName("input")
                            try
                                use d = JsonDocument.Parse(if String.IsNullOrWhiteSpace c.ArgumentsJson then "{}" else c.ArgumentsJson)
                                d.RootElement.WriteTo(w)
                            with :? JsonException as ex -> raise (AiException(AiError.Decode($"Tool '{c.Name}' has invalid arguments JSON: {ex.Message}"), ex))
                            w.WriteEndObject()
                        w.WriteEndArray()
                    | _ -> writeUserParts w m
                    w.WriteEndObject()
                    messageIndex <- messageIndex + 1
        w.WriteEndArray()
        if systems.Count > 0 then w.WritePropertyName("system"); w.WriteStringValue(String.Join("\n", systems))
        if request.Temperature.HasValue then w.WriteNumber("temperature", request.Temperature.Value)
        if request.TopP.HasValue then w.WriteNumber("top_p", request.TopP.Value)
        if not (isNull request.StopSequences) && request.StopSequences.Count > 0 then
            w.WritePropertyName("stop_sequences")
            w.WriteStartArray()
            for s in request.StopSequences do w.WriteStringValue(s)
            w.WriteEndArray()
        if not (isNull request.Tools) && request.Tools.Count > 0 then
            w.WritePropertyName("tools"); w.WriteStartArray()
            for tool in request.Tools do
                w.WriteStartObject(); w.WriteString("name", tool.Name); if not (String.IsNullOrWhiteSpace tool.Description) then w.WriteString("description", tool.Description)
                w.WritePropertyName("input_schema")
                if String.IsNullOrWhiteSpace tool.ParametersJson then w.WriteStartObject(); w.WriteString("type", "object"); w.WriteEndObject()
                else
                    try
                        use d = JsonDocument.Parse(tool.ParametersJson)
                        d.RootElement.WriteTo(w)
                    with :? JsonException as ex -> raise (AiException(AiError.Decode($"Tool '{tool.Name}' has invalid schema JSON: {ex.Message}"), ex))
                w.WriteEndObject()
            w.WriteEndArray()
        let hasBudget = request.ReasoningBudgetTokens.HasValue
        let effort = request.ReasoningEffort
        if hasBudget || not (String.IsNullOrWhiteSpace effort) then
            let lower = model.ToLowerInvariant()
            let adaptive = lower.Contains("4-6") || lower.Contains("4-7") || lower.Contains("4-8") || lower.Contains("opus-5") || lower.Contains("sonnet-5") || lower.Contains("fable") || lower.Contains("mythos")
            if hasBudget && adaptive then raise (AiException("This Claude model uses adaptive thinking; explicit reasoning budgets are unsupported."))
            let opus45 = lower.Contains("opus-4-5") || lower.Contains("opus-4.5")
            let legacyThinking = lower.Contains("3-7") || lower.Contains("4-0") || lower.Contains("4-1") || opus45
            if hasBudget && not legacyThinking then raise (AiException("This Claude model does not support explicit reasoning budgets."))
            if not (String.IsNullOrWhiteSpace effort) && not adaptive && not (hasBudget && opus45) then raise (AiException("This Claude model does not advertise adaptive reasoning effort; use ReasoningBudgetTokens."))
            if hasBudget then
                if request.ReasoningBudgetTokens.Value < 1 then raise (AiException("Claude reasoning budget must be positive."))
                if maxTokens <= request.ReasoningBudgetTokens.Value then raise (AiException("Claude max_tokens must exceed reasoning budget_tokens."))
                w.WritePropertyName("thinking"); w.WriteStartObject(); w.WriteString("type", "enabled"); w.WriteNumber("budget_tokens", request.ReasoningBudgetTokens.Value); w.WriteEndObject()
                if opus45 && not (String.IsNullOrWhiteSpace effort) then
                    w.WritePropertyName("output_config"); w.WriteStartObject(); w.WriteString("effort", effort.ToLowerInvariant()); w.WriteEndObject()
            else
                w.WritePropertyName("thinking"); w.WriteStartObject(); w.WriteString("type", "adaptive"); w.WriteEndObject()
                w.WritePropertyName("output_config"); w.WriteStartObject(); w.WriteString("effort", effort.ToLowerInvariant()); w.WriteEndObject()
        w.WriteEndObject(); w.Flush(); Encoding.UTF8.GetString(ms.ToArray())

    member _.ParseResponse(body: string) =
        let doc = try JsonDocument.Parse(body) with :? JsonException as ex -> raise (jsonError "Invalid Anthropic response JSON: " ex)
        use doc = doc
        let root = doc.RootElement
        match ProviderJson.prop root "error" with
        | Some error -> raise (AiException(AiError.Decode("Anthropic API error: " + ProviderJson.str error "message" "unknown error")))
        | _ -> ()
        let parts = ResizeArray<ContentPart>()
        let calls = ResizeArray<ToolCall>()
        let raw = ResizeArray<JsonElement>()
        let mutable finish = FinishReason.Unknown
        match ProviderJson.prop root "content" with
        | Some content when content.ValueKind = JsonValueKind.Array ->
            for b in content.EnumerateArray() do
                raw.Add(b.Clone())
                match ProviderJson.str b "type" "" with
                | "text" -> parts.Add(TextPart(ProviderJson.str b "text" ""))
                | "tool_use" ->
                    let input = match ProviderJson.prop b "input" with Some x -> x.GetRawText() | _ -> "{}"
                    calls.Add(ToolCall(ProviderJson.str b "id" ("claude_tool_" + string calls.Count), ProviderJson.str b "name" "", input))
                | _ -> ()
        | _ -> ()
        match ProviderJson.str root "stop_reason" "" with
        | "end_turn" | "stop_sequence" -> finish <- FinishReason.Stop
        | "max_tokens" -> finish <- FinishReason.Length
        | "tool_use" -> finish <- FinishReason.ToolCalls
        | "refusal" -> finish <- FinishReason.ContentFilter
        | _ -> ()
        let msg = ChatMessage(Role.Assistant, parts :> IReadOnlyList<ContentPart>, null, calls :> IReadOnlyList<ToolCall>, null)
        if raw.Count > 0 then
            use ms = new MemoryStream()
            use w = new Utf8JsonWriter(ms)
            w.WriteStartArray()
            for b in raw do b.WriteTo(w)
            w.WriteEndArray()
            w.Flush()
            msg.ProviderStateJson <- providerState raw model
        let u = match ProviderJson.prop root "usage" with Some x -> x | _ -> Unchecked.defaultof<JsonElement>
        let i = inputTokenCount u
        let o = ProviderJson.int64 u "output_tokens"
        let total = match ProviderJson.prop u "total_tokens" with Some _ -> ProviderJson.int64 u "total_tokens" | _ -> i + o
        ChatResponse(ProviderJson.str root "id" "", ProviderJson.str root "model" model, msg, Usage(i,o,total), finish)

    member _.CreateStreamParser() =
        let mutable active = true
        let mutable completed = false
        let mutable finish = FinishReason.Unknown
        let mutable usage = Usage()
        let mutable blockType = ""
        let mutable blockIndex = 0
        let mutable toolId = ""
        let mutable toolName = ""
        let blocks = SortedDictionary<int, JsonObject>()
        let toolArguments = Dictionary<int, StringBuilder>()
        let toolIds = Dictionary<int, string>()
        let toolNames = Dictionary<int, string>()
        let makeState () =
            use ms = new MemoryStream()
            use w = new Utf8JsonWriter(ms)
            w.WriteStartObject()
            w.WriteString("protocol", stateProtocolId)
            w.WriteString("model", model)
            w.WritePropertyName("content")
            w.WriteStartArray()
            for KeyValue(_, block) in blocks do block.WriteTo(w)
            w.WriteEndArray()
            w.WriteEndObject()
            w.Flush()
            Encoding.UTF8.GetString(ms.ToArray())
        let cb (eventName: string) (payload: string) =
            try
                use doc = JsonDocument.Parse(payload)
                let root = doc.RootElement
                let kind = if String.IsNullOrWhiteSpace eventName then ProviderJson.str root "type" "" else eventName
                let evts = ResizeArray<ChatStreamEvent>()
                match kind with
                | "message_start" ->
                    match ProviderJson.prop root "message" with
                    | Some m ->
                        match ProviderJson.prop m "usage" with
                        | Some u ->
                            let input = inputTokenCount u
                            let output = ProviderJson.int64 u "output_tokens"
                            usage <- Usage(input, output, input + output)
                            evts.Add(UsageUpdate usage)
                        | _ -> ()
                    | _ -> ()
                | "content_block_start" ->
                    blockIndex <- match ProviderJson.prop root "index" with Some x when x.ValueKind = JsonValueKind.Number -> x.GetInt32() | _ -> blockIndex
                    match ProviderJson.prop root "content_block" with
                    | Some b ->
                        blockType <- ProviderJson.str b "type" ""
                        let block = JsonNode.Parse(b.GetRawText()).AsObject()
                        blocks[blockIndex] <- block
                        if blockType = "tool_use" then
                            toolArguments[blockIndex] <- StringBuilder()
                            toolIds[blockIndex] <- ProviderJson.str b "id" ""
                            toolNames[blockIndex] <- ProviderJson.str b "name" ""
                        if blockType = "tool_use" then
                            toolId <- toolIds[blockIndex]
                            toolName <- toolNames[blockIndex]
                            evts.Add(ToolCallDelta(blockIndex, toolId, toolName, ""))
                    | _ -> ()
                | "content_block_delta" ->
                    blockIndex <- match ProviderJson.prop root "index" with Some x when x.ValueKind = JsonValueKind.Number -> x.GetInt32() | _ -> blockIndex
                    if toolIds.ContainsKey(blockIndex) then toolId <- toolIds[blockIndex]
                    if toolNames.ContainsKey(blockIndex) then toolName <- toolNames[blockIndex]
                    match ProviderJson.prop root "delta" with
                    | Some d ->
                        let deltaType = ProviderJson.str d "type" ""
                        match deltaType with
                        | "text_delta" ->
                            let text = ProviderJson.str d "text" ""
                            evts.Add(TextDelta(text))
                            match blocks.TryGetValue(blockIndex) with
                            | true, block ->
                                let old = if isNull block["text"] then "" else block["text"].GetValue<string>()
                                block["text"] <- JsonValue.Create(old + text)
                            | _ -> ()
                        | "input_json_delta" ->
                            let partial = ProviderJson.str d "partial_json" ""
                            match toolArguments.TryGetValue(blockIndex) with
                            | true, buffer -> buffer.Append(partial) |> ignore
                            | _ -> ()
                            evts.Add(ToolCallDelta(blockIndex, toolId, toolName, partial))
                        | "thinking_delta" ->
                            match blocks.TryGetValue(blockIndex) with
                            | true, block ->
                                let text = ProviderJson.str d "thinking" ""
                                let old = if isNull block["thinking"] then "" else block["thinking"].GetValue<string>()
                                block["thinking"] <- JsonValue.Create(old + text)
                            | _ -> ()
                        | "signature_delta" ->
                            match blocks.TryGetValue(blockIndex) with
                            | true, block ->
                                let signature = ProviderJson.str d "signature" ""
                                let old = if isNull block["signature"] then "" else block["signature"].GetValue<string>()
                                block["signature"] <- JsonValue.Create(old + signature)
                            | _ -> ()
                        | "redacted_thinking_delta" -> ()
                        | _ -> ()
                    | _ -> ()
                | "content_block_stop" ->
                    blockIndex <- match ProviderJson.prop root "index" with Some x when x.ValueKind = JsonValueKind.Number -> x.GetInt32() | _ -> blockIndex
                    match toolArguments.TryGetValue(blockIndex) with
                    | true, arguments ->
                        match blocks.TryGetValue(blockIndex) with
                        | true, block ->
                            try
                                let node = JsonNode.Parse(arguments.ToString())
                                block["input"] <- node
                            with :? JsonException as ex -> raise (jsonError "Invalid streamed Claude tool arguments JSON: " ex)
                        | _ -> ()
                    | _ -> ()
                | "message_delta" ->
                    match ProviderJson.prop root "delta" with
                    | Some d ->
                        match ProviderJson.str d "stop_reason" "" with
                        | "end_turn" | "stop_sequence" -> finish <- FinishReason.Stop
                        | "max_tokens" -> finish <- FinishReason.Length
                        | "tool_use" -> finish <- FinishReason.ToolCalls
                        | "refusal" -> finish <- FinishReason.ContentFilter
                        | _ -> ()
                    | _ -> ()
                    match ProviderJson.prop root "usage" with
                    | Some u ->
                        let o = ProviderJson.int64 u "output_tokens"
                        let input = inputTokenCount u
                        let input = if input = 0L then usage.InputTokens else input
                        usage <- Usage(input, o, input + o)
                        evts.Add(UsageUpdate usage)
                    | _ -> ()
                | "message_stop" ->
                    completed <- true
                    active <- false
                    evts.Add(ProviderStateUpdate(makeState()))
                    evts.Add(Completed(finish, usage))
                | "error" ->
                    let error = match ProviderJson.prop root "error" with Some e -> ProviderJson.str e "message" "unknown error" | _ -> "unknown error"
                    raise (AiException(AiError.Decode("Anthropic stream error: " + error)))
                | _ -> ()
                ProviderJson.events evts
            with :? AiException -> reraise() | ex -> raise (jsonError "Failed to decode Anthropic stream event: " ex)
        let frame = SseFrameParser(cb)
        { new IChatStreamParser with
            member _.PushLine(line) = if completed then ProviderJson.emptyEvents else frame.PushLine(line)
            member _.Finish() =
                let evts = ResizeArray<ChatStreamEvent>(frame.Finish())
                if active || not completed then raise (AiException(AiError.Decode "Anthropic stream ended before message_stop."))
                ProviderJson.events evts }

    member this.SerializeRequest(request) = this.Serialize(request)
    member this.ParseResponseBody(body) = this.ParseResponse(body)
    member this.MakeStreamParser() = this.CreateStreamParser()

type internal AnthropicBackendBase(model: string, accountId: string, credentials: ICredentialProvider, options: ProviderOptions, vertex: bool) =
    let codec = AnthropicCodec(model, (if vertex then "vertex-claude" else "anthropic"), vertex)
    let credentials = if isNull credentials && not (isNull options) then options.Credentials else credentials
    let baseUrl = if not (isNull options) && not (String.IsNullOrWhiteSpace options.BaseUrl) then options.BaseUrl.TrimEnd('/') else null
    let endpoint (requestModel: string) (stream: bool) =
        let suffix = if stream then ":streamRawPredict" else ":rawPredict"
        if vertex then
            let project = if not (isNull options) && not (String.IsNullOrWhiteSpace options.ProjectId) then options.ProjectId else Environment.GetEnvironmentVariable("GOOGLE_VERTEX_PROJECT")
            let location = if not (isNull options) && not (String.IsNullOrWhiteSpace options.Location) then options.Location else Environment.GetEnvironmentVariable("GOOGLE_VERTEX_LOCATION")
            if String.IsNullOrWhiteSpace project || String.IsNullOrWhiteSpace location then raise (AiException("Vertex Claude requires ProviderOptions.ProjectId and Location or GOOGLE_VERTEX_PROJECT and GOOGLE_VERTEX_LOCATION."))
            let host = if location = "global" then "aiplatform.googleapis.com" else location + "-aiplatform.googleapis.com"
            let root = if isNull baseUrl then "https://" + host else baseUrl
            root + $"/v1/projects/{Uri.EscapeDataString(project)}/locations/{Uri.EscapeDataString(location)}/publishers/anthropic/models/{Uri.EscapeDataString(requestModel)}" + suffix
        else
            let apiRoot =
                if isNull baseUrl then "https://api.anthropic.com/v1"
                elif baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) then baseUrl
                else baseUrl + "/v1"
            apiRoot + "/messages"
    interface IChatBackend with
        member _.PrepareRequest(request, stream, ct) = task {
            let requestModel = if String.IsNullOrWhiteSpace request.Model then model else request.Model
            let requestCodec = AnthropicCodec(requestModel, (if vertex then "vertex-claude" else "anthropic"), vertex)
            let headers = Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            headers["Content-Type"] <- "application/json"
            if not vertex then headers["anthropic-version"] <- "2023-06-01"
            if not (isNull credentials) then
                let! supplied = credentials.GetHeaders(accountId, ct)
                if not (isNull supplied) then
                    for KeyValue(k, v) in supplied do headers[k] <- v
            if not vertex && not (headers.ContainsKey("x-api-key")) then
                match Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") with null | "" -> () | key -> headers["x-api-key"] <- key
            let body = JsonNode.Parse(requestCodec.Serialize(request)).AsObject()
            if vertex then body["anthropic_version"] <- JsonValue.Create("vertex-2023-10-16")
            if stream then body["stream"] <- JsonValue.Create(true)
            let body = body.ToJsonString()
            return PreparedChatRequest(endpoint requestModel stream, headers :> IReadOnlyDictionary<string,string>, body)
        }
        member _.ParseResponse(body) = codec.ParseResponse(body)
        member _.CreateStreamParser() = codec.MakeStreamParser()

[<Sealed>]
type AnthropicBackend(model: string, accountId: string, credentials: ICredentialProvider, options: ProviderOptions) =
    let inner = AnthropicBackendBase(model, accountId, credentials, options, false)
    interface IChatBackend with
        member _.PrepareRequest(r, s, ct) = (inner :> IChatBackend).PrepareRequest(r, s, ct)
        member _.ParseResponse(b) = (inner :> IChatBackend).ParseResponse(b)
        member _.CreateStreamParser() = (inner :> IChatBackend).CreateStreamParser()
    interface IRequestScopedBackend with
        member this.ResolveBackend(requestModel) =
            if String.Equals(model, requestModel, StringComparison.Ordinal) then this :> IChatBackend
            else AnthropicBackend(requestModel, accountId, credentials, options) :> IChatBackend

[<Sealed>]
type VertexAnthropicBackend(model: string, accountId: string, credentials: ICredentialProvider, options: ProviderOptions) =
    let inner = AnthropicBackendBase(model, accountId, credentials, options, true)
    interface IChatBackend with
        member _.PrepareRequest(r, s, ct) = (inner :> IChatBackend).PrepareRequest(r, s, ct)
        member _.ParseResponse(b) = (inner :> IChatBackend).ParseResponse(b)
        member _.CreateStreamParser() = (inner :> IChatBackend).CreateStreamParser()
    interface IRequestScopedBackend with
        member this.ResolveBackend(requestModel) =
            if String.Equals(model, requestModel, StringComparison.Ordinal) then this :> IChatBackend
            else VertexAnthropicBackend(requestModel, accountId, credentials, options) :> IChatBackend
