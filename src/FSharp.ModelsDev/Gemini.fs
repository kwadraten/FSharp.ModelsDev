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

module internal ProviderJson =
    let prop (e: JsonElement) (name: string) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if e.ValueKind = JsonValueKind.Object && e.TryGetProperty(name, &value) then Some value else None
    let str (e: JsonElement) (name: string) (fallback: string) =
        match prop e name with
        | Some v when v.ValueKind = JsonValueKind.String -> if isNull (v.GetString()) then fallback else v.GetString()
        | _ -> fallback
    let int64 (e: JsonElement) (name: string) =
        match prop e name with
        | Some v when v.ValueKind = JsonValueKind.Number -> match v.TryGetInt64() with true, n -> n | _ -> 0L
        | _ -> 0L
    let parseError (context: string) (ex: exn) = AiException(AiError.Decode(context + ex.Message), ex)
    let readRoot (body: string) =
        try JsonDocument.Parse(body)
        with :? JsonException as ex -> raise (parseError "Invalid provider JSON: " ex)
    let emptyEvents = Array.empty<ChatStreamEvent> :> IReadOnlyList<ChatStreamEvent>
    let events (items: ResizeArray<ChatStreamEvent>) = items.ToArray() :> IReadOnlyList<ChatStreamEvent>
    let finish (v: string) =
        match v with
        | "STOP" | "END_TURN" -> FinishReason.Stop
        | "MAX_TOKENS" -> FinishReason.Length
        | "SAFETY" | "RECITATION" | "BLOCKLIST" | "PROHIBITED_CONTENT" -> FinishReason.ContentFilter
        | "TOOL_USE" -> FinishReason.ToolCalls
        | _ -> FinishReason.Unknown

/// Shared wire codec for Gemini generateContent, Vertex Gemini and Antigravity's Gemini payloads.
[<Sealed>]
type internal GeminiCodec(model: string, stateProtocolId: string) =
    let legacySchema (text: string) (toolName: string) : JsonObject =
        let parse () =
            try JsonNode.Parse(text)
            with :? JsonException as ex -> raise (AiException(AiError.Decode($"Tool '{toolName}' has invalid parameters JSON: {ex.Message}"), ex))
        let root = parse()
        let metadata = set [ "$schema"; "$id"; "$comment"; "$defs"; "definitions"; "title"; "examples"; "default"; "deprecated"; "readOnly"; "writeOnly" ]
        let allowed = set [ "type"; "description"; "properties"; "required"; "enum"; "items"; "additionalProperties"; "minimum"; "maximum"; "exclusiveMinimum"; "exclusiveMaximum"; "minLength"; "maxLength"; "pattern"; "format"; "minItems"; "maxItems"; "uniqueItems"; "const"; "$ref" ]
        let resolve (pointer: string) =
            if not (pointer.StartsWith("#/", StringComparison.Ordinal)) then raise (AiException($"Tool '{toolName}' uses unsupported non-local schema reference '{pointer}'."))
            let mutable current = root
            for rawSegment in pointer.Substring(2).Split('/') do
                let segment = rawSegment.Replace("~1", "/").Replace("~0", "~")
                match current with
                | :? JsonObject as obj -> current <- obj[segment]
                | :? JsonArray as arr ->
                    match Int32.TryParse(segment) with
                    | true, index when index >= 0 && index < arr.Count -> current <- arr[index]
                    | _ -> current <- null
                | _ -> current <- null
                if isNull current then raise (AiException($"Tool '{toolName}' schema reference '{pointer}' does not resolve."))
            current
        let rec convert (referenceStack: Set<string>) (node: JsonNode) : JsonNode =
            match node with
            | :? JsonObject as obj ->
                let output = JsonObject()
                if obj.ContainsKey("$ref") then
                    let reference = obj["$ref"].GetValue<string>()
                    let unsupportedSiblings =
                        obj
                        |> Seq.map (fun pair -> pair.Key)
                        |> Seq.filter (fun key -> key <> "$ref" && not (metadata.Contains key))
                        |> Seq.toArray
                    if unsupportedSiblings.Length > 0 then
                        let siblingNames = String.Join(", ", unsupportedSiblings)
                        raise (AiException($"Tool '{toolName}' schema reference '{reference}' has unsupported validation siblings: {siblingNames}."))
                    if referenceStack.Contains reference then
                        raise (AiException($"Tool '{toolName}' schema contains a recursive local reference cycle at '{reference}'."))
                    let resolved = convert (referenceStack.Add reference) (resolve reference)
                    match resolved with
                    | :? JsonObject as resolvedObject ->
                        for KeyValue(key, value) in resolvedObject do output[key] <- if isNull value then null else JsonNode.Parse(value.ToJsonString())
                    | _ -> raise (AiException($"Tool '{toolName}' schema reference '{reference}' must resolve to an object."))
                if obj.ContainsKey("const") && obj.ContainsKey("enum") then
                    raise (AiException($"Tool '{toolName}' schema combines const and enum; Antigravity conversion rejects this combination rather than changing its intersection semantics."))
                for KeyValue(key, value) in obj do
                    if key = "const" then
                        let enumValues = JsonArray()
                        let enumValue: JsonNode = if isNull value then null else JsonNode.Parse(value.ToJsonString())
                        enumValues.Add(enumValue)
                        output["enum"] <- enumValues
                    elif key <> "$ref" && not (metadata.Contains key) then
                        if not (allowed.Contains key) then raise (AiException($"Tool '{toolName}' uses unsupported JSON Schema keyword '{key}' in Antigravity mode."))
                        if key <> "const" then
                            output[key] <-
                                if key = "properties" then
                                    match value with
                                    | :? JsonObject as properties ->
                                        let converted = JsonObject()
                                        for KeyValue(propertyName, propertySchema) in properties do converted[propertyName] <- convert referenceStack propertySchema
                                        converted :> JsonNode
                                    | _ -> if isNull value then null else JsonNode.Parse(value.ToJsonString())
                                elif key = "items" || key = "additionalProperties" && value :? JsonObject then convert referenceStack value
                                elif isNull value then null
                                else JsonNode.Parse(value.ToJsonString())
                output :> JsonNode
            | _ -> if isNull node then null else JsonNode.Parse(node.ToJsonString())
        match convert Set.empty root with
        | :? JsonObject as result -> result
        | _ -> raise (AiException($"Tool '{toolName}' schema must be a JSON object."))

    let validState (message: ChatMessage) =
        if String.IsNullOrEmpty message.ProviderStateJson then None
        else
            use state = ProviderJson.readRoot message.ProviderStateJson
            let root = state.RootElement
            if ProviderJson.str root "protocol" "" <> stateProtocolId || ProviderJson.str root "model" "" <> model then None
            else
                match ProviderJson.prop root "parts" with
                | Some parts when parts.ValueKind = JsonValueKind.Array -> Some (parts.GetRawText())
                | _ -> None

    let providerState (parts: ResizeArray<JsonElement>) (stateModel: string) =
        use ms = new MemoryStream()
        use w = new Utf8JsonWriter(ms)
        w.WriteStartObject()
        w.WriteString("protocol", stateProtocolId)
        w.WriteString("model", stateModel)
        w.WritePropertyName("parts")
        w.WriteStartArray()
        for part in parts do part.WriteTo(w)
        w.WriteEndArray()
        w.WriteEndObject()
        w.Flush()
        Encoding.UTF8.GetString(ms.ToArray())

    let writePart (w: Utf8JsonWriter) (part: ContentPart) =
        match part with
        | :? TextPart as t -> w.WriteStartObject(); w.WriteString("text", t.Text); w.WriteEndObject()
        | :? ImagePart as image ->
            let u = image.Url
            if isNull u then raise (AiException("Gemini image input has no URL."))
            if u.StartsWith("data:", StringComparison.OrdinalIgnoreCase) then
                let comma = u.IndexOf(',')
                if comma < 0 || not (u.Substring(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) then
                    raise (AiException("Gemini image data URI must contain base64-encoded bytes."))
                let mime = if String.IsNullOrWhiteSpace image.MediaType then u.Substring(5, u.IndexOf(';') - 5) else image.MediaType
                w.WriteStartObject(); w.WritePropertyName("inlineData"); w.WriteStartObject(); w.WriteString("mimeType", mime); w.WriteString("data", u.Substring(comma + 1)); w.WriteEndObject(); w.WriteEndObject()
            elif u.StartsWith("gs://", StringComparison.OrdinalIgnoreCase) || Uri.IsWellFormedUriString(u, UriKind.Absolute) then
                w.WriteStartObject(); w.WritePropertyName("fileData"); w.WriteStartObject(); if not (String.IsNullOrWhiteSpace image.MediaType) then w.WriteString("mimeType", image.MediaType); w.WriteString("fileUri", u); w.WriteEndObject(); w.WriteEndObject()
            else raise (AiException("Gemini image input must be a base64 data URI, gs:// URI, or absolute supported media URI."))
        | _ -> raise (AiException("Gemini supports text and image content parts only."))

    let writeParts (w: Utf8JsonWriter) (parts: IReadOnlyList<ContentPart>) =
        w.WriteStartArray()
        if not (isNull parts) then for p in parts do writePart w p
        w.WriteEndArray()

    let writeMessage (w: Utf8JsonWriter) (message: ChatMessage) =
        w.WriteStartObject()
        w.WriteString("role", if message.Role = Role.User then "user" else "model")
        w.WritePropertyName("parts")
        let preserved = if message.Role = Role.Assistant then validState message else None
        match preserved with
        | Some raw ->
            try
                use d = JsonDocument.Parse(raw)
                d.RootElement.WriteTo(w)
            with :? JsonException as ex -> raise (AiException(AiError.Decode("Invalid preserved Gemini provider state: " + ex.Message), ex))
        | None ->
            w.WriteStartArray()
            if not (isNull message.Parts) then for p in message.Parts do writePart w p
            if not (isNull message.ToolCalls) then
                for call in message.ToolCalls do
                    w.WriteStartObject(); w.WritePropertyName("functionCall"); w.WriteStartObject()
                    if not (String.IsNullOrWhiteSpace call.Id) && not (call.Id.StartsWith("gemini_synth_", StringComparison.Ordinal)) then w.WriteString("id", call.Id)
                    w.WriteString("name", call.Name); w.WritePropertyName("args")
                    try
                        use d = JsonDocument.Parse(if String.IsNullOrWhiteSpace call.ArgumentsJson then "{}" else call.ArgumentsJson)
                        d.RootElement.WriteTo(w)
                    with :? JsonException as ex -> raise (AiException(AiError.Decode($"Invalid arguments JSON for tool '{call.Name}': {ex.Message}"), ex))
                    w.WriteEndObject(); w.WriteEndObject()
            w.WriteEndArray()
        w.WriteEndObject()

    member _.Serialize(request: ChatRequest) =
        use ms = new MemoryStream()
        use w = new Utf8JsonWriter(ms)
        w.WriteStartObject()
        let systems = ResizeArray<ContentPart>()
        let writeToolResult (message: ChatMessage) =
            let callId = message.ToolCallId
            let prior =
                request.Messages
                |> Seq.takeWhile (fun x -> not (Object.ReferenceEquals(x, message)))
                |> Seq.collect (fun x -> if isNull x.ToolCalls then Seq.empty else x.ToolCalls :> seq<ToolCall>)
                |> Seq.filter (fun c -> c.Id = callId)
                |> Seq.rev
                |> Seq.tryHead
            match prior with
            | None -> raise (AiException($"Gemini tool result '{callId}' has no preceding matching tool call."))
            | Some call ->
                w.WriteStartObject()
                w.WritePropertyName("functionResponse")
                w.WriteStartObject()
                if not (String.IsNullOrWhiteSpace call.Id) && not (call.Id.StartsWith("gemini_synth_", StringComparison.Ordinal)) then w.WriteString("id", call.Id)
                w.WriteString("name", call.Name)
                w.WritePropertyName("response")
                try
                    use d = JsonDocument.Parse(if String.IsNullOrEmpty message.Text then "{}" else message.Text)
                    if d.RootElement.ValueKind = JsonValueKind.Object then d.RootElement.WriteTo(w)
                    else
                        w.WriteStartObject()
                        w.WritePropertyName("result")
                        d.RootElement.WriteTo(w)
                        w.WriteEndObject()
                with :? JsonException ->
                    w.WriteStartObject()
                    w.WriteString("result", message.Text)
                    w.WriteEndObject()
                w.WriteEndObject()
                w.WriteEndObject()
        w.WritePropertyName("contents"); w.WriteStartArray()
        if not (isNull request.Messages) then
            let mutable messageIndex = 0
            while messageIndex < request.Messages.Count do
                let m = request.Messages[messageIndex]
                if m.Role = Role.System then
                    if not (isNull m.Parts) then for p in m.Parts do systems.Add(p)
                    messageIndex <- messageIndex + 1
                elif m.Role = Role.Tool then
                    // Google pairs parallel tool results in one user turn.
                    w.WriteStartObject()
                    w.WriteString("role", "user")
                    w.WritePropertyName("parts")
                    w.WriteStartArray()
                    while messageIndex < request.Messages.Count && request.Messages[messageIndex].Role = Role.Tool do
                        writeToolResult request.Messages[messageIndex]
                        messageIndex <- messageIndex + 1
                    w.WriteEndArray()
                    w.WriteEndObject()
                else writeMessage w m
                if m.Role <> Role.Tool && m.Role <> Role.System then messageIndex <- messageIndex + 1
        w.WriteEndArray()
        if systems.Count > 0 then w.WritePropertyName("systemInstruction"); w.WriteStartObject(); w.WritePropertyName("parts"); writeParts w (systems :> IReadOnlyList<ContentPart>); w.WriteEndObject()
        w.WritePropertyName("generationConfig"); w.WriteStartObject()
        if request.Temperature.HasValue then w.WriteNumber("temperature", request.Temperature.Value)
        if request.TopP.HasValue then w.WriteNumber("topP", request.TopP.Value)
        if request.MaxTokens.HasValue then w.WriteNumber("maxOutputTokens", request.MaxTokens.Value)
        if not (isNull request.StopSequences) && request.StopSequences.Count > 0 then
            w.WritePropertyName("stopSequences")
            w.WriteStartArray()
            for x in request.StopSequences do w.WriteStringValue(x)
            w.WriteEndArray()
        if not (String.IsNullOrWhiteSpace request.ReasoningEffort) || request.ReasoningBudgetTokens.HasValue then
            let m = model.ToLowerInvariant()
            let modern = m.Contains("gemini-3") || m.Contains("gemini-4")
            if modern && request.ReasoningBudgetTokens.HasValue then raise (AiException("Gemini 3/4 models use reasoning effort levels; explicit thinking budgets are unsupported."))
            if not modern && request.ReasoningBudgetTokens.HasValue && not (m.Contains("gemini-2.5") || m.Contains("robotics-er")) then
                raise (AiException("This Gemini model does not support thinkingBudget; explicit reasoning budgets require a Gemini 2.5 thinking model."))
            if not modern && not (String.IsNullOrWhiteSpace request.ReasoningEffort) then raise (AiException("This Gemini model family does not accept reasoning effort levels; use ReasoningBudgetTokens."))
            w.WritePropertyName("thinkingConfig"); w.WriteStartObject()
            if not (String.IsNullOrWhiteSpace request.ReasoningEffort) then w.WriteString("thinkingLevel", request.ReasoningEffort.ToUpperInvariant())
            if request.ReasoningBudgetTokens.HasValue then w.WriteNumber("thinkingBudget", request.ReasoningBudgetTokens.Value)
            w.WriteEndObject()
        w.WriteEndObject()
        if not (isNull request.Tools) && request.Tools.Count > 0 then
            w.WritePropertyName("tools"); w.WriteStartArray(); w.WriteStartObject(); w.WritePropertyName("functionDeclarations"); w.WriteStartArray()
            for tool in request.Tools do
                w.WriteStartObject(); w.WriteString("name", tool.Name); if not (String.IsNullOrWhiteSpace tool.Description) then w.WriteString("description", tool.Description)
                if not (String.IsNullOrWhiteSpace tool.ParametersJson) then
                    try
                        use d = JsonDocument.Parse(tool.ParametersJson)
                        if d.RootElement.ValueKind <> JsonValueKind.Object then raise (AiException($"Tool '{tool.Name}' schema must be a JSON object."))
                        if stateProtocolId = "antigravity" then
                            w.WritePropertyName("parameters")
                            (legacySchema tool.ParametersJson tool.Name).WriteTo(w)
                        else
                            w.WritePropertyName("parametersJsonSchema")
                            d.RootElement.WriteTo(w)
                    with
                    | :? AiException -> reraise()
                    | :? JsonException as ex -> raise (AiException(AiError.Decode($"Tool '{tool.Name}' has invalid parameters JSON: {ex.Message}"), ex))
                w.WriteEndObject()
            w.WriteEndArray(); w.WriteEndObject(); w.WriteEndArray()
        w.WriteEndObject(); w.Flush(); Encoding.UTF8.GetString(ms.ToArray())

    member _.ParseResponse(body: string) =
        use doc = ProviderJson.readRoot body
        let root = doc.RootElement
        match ProviderJson.prop root "error" with
        | Some error -> raise (AiException(AiError.Decode("Gemini API error: " + ProviderJson.str error "message" "unknown error")))
        | _ -> ()
        let root = match ProviderJson.prop root "response" with Some r when r.ValueKind = JsonValueKind.Object -> r | _ -> root
        let candidates = ProviderJson.prop root "candidates"
        let candidate = match candidates with Some c when c.ValueKind = JsonValueKind.Array && c.GetArrayLength() > 0 -> c.[0] | _ -> Unchecked.defaultof<JsonElement>
        let parts = ResizeArray<ContentPart>()
        let calls = ResizeArray<ToolCall>()
        let rawParts = ResizeArray<JsonElement>()
        let responseId = ProviderJson.str root "responseId" "response"
        match ProviderJson.prop candidate "content" with
        | Some content ->
            match ProviderJson.prop content "parts" with
            | Some ps when ps.ValueKind = JsonValueKind.Array ->
                for p in ps.EnumerateArray() do
                    rawParts.Add(p.Clone())
                    let thought = match ProviderJson.prop p "thought" with Some x when x.ValueKind = JsonValueKind.True -> true | _ -> false
                    match ProviderJson.prop p "text" with
                    | Some t when t.ValueKind = JsonValueKind.String && not thought -> parts.Add(TextPart(t.GetString()))
                    | _ -> ()
                    match ProviderJson.prop p "functionCall" with
                    | Some fc ->
                        let id = ProviderJson.str fc "id" null
                        let stableId = if String.IsNullOrWhiteSpace id then $"gemini_synth_{responseId}_{calls.Count}" else id
                        let name = ProviderJson.str fc "name" ""
                        let args = match ProviderJson.prop fc "args" with Some a -> a.GetRawText() | _ -> "{}"
                        calls.Add(ToolCall(stableId, name, args))
                    | _ -> ()
            | _ -> ()
        | _ -> ()
        let message = ChatMessage(Role.Assistant, parts :> IReadOnlyList<ContentPart>, null, calls :> IReadOnlyList<ToolCall>, null)
        if rawParts.Count > 0 then
            use ms = new MemoryStream()
            use w = new Utf8JsonWriter(ms)
            w.WriteStartArray()
            for p in rawParts do p.WriteTo(w)
            w.WriteEndArray()
            w.Flush()
            message.ProviderStateJson <- providerState rawParts model
        let usageRoot = match ProviderJson.prop root "usageMetadata" with Some u -> u | _ -> Unchecked.defaultof<JsonElement>
        let input = ProviderJson.int64 usageRoot "promptTokenCount"
        let output = ProviderJson.int64 usageRoot "candidatesTokenCount"
        let total = ProviderJson.int64 usageRoot "totalTokenCount"
        let reason =
            let candidateReason = ProviderJson.str candidate "finishReason" ""
            if not (String.IsNullOrWhiteSpace candidateReason) then candidateReason
            else
                match ProviderJson.prop root "promptFeedback" with
                | Some feedback when not (String.IsNullOrWhiteSpace(ProviderJson.str feedback "blockReason" "")) -> "SAFETY"
                | _ -> ""
        let parsedFinish = ProviderJson.finish reason
        let parsedFinish = if calls.Count > 0 && parsedFinish = FinishReason.Stop then FinishReason.ToolCalls else parsedFinish
        ChatResponse(ProviderJson.str root "responseId" "", ProviderJson.str root "modelVersion" model, message, Usage(input, output, (if total = 0L then input + output else total)), parsedFinish)

    member _.CreateStreamParser() =
        let mutable terminal = false
        let mutable gotUsage = false
        let mutable completionEmitted = false
        let mutable usage = Usage()
        let mutable finishReason = FinishReason.Unknown
        let mutable index = 0
        let mutable sawFunctionCall = false
        let streamParts = ResizeArray<JsonElement>()
        let cb (_event: string) (payload: string) =
            try
                if payload = "[DONE]" then
                    ProviderJson.emptyEvents
                else
                    use doc = JsonDocument.Parse(payload)
                    let root0 = doc.RootElement
                    match ProviderJson.prop root0 "error" with
                    | Some error -> raise (AiException(AiError.Decode("Gemini stream error: " + ProviderJson.str error "message" "unknown error")))
                    | _ -> ()
                    let root = match ProviderJson.prop root0 "response" with Some r -> r | _ -> root0
                    let events = ResizeArray<ChatStreamEvent>()
                    match ProviderJson.prop root "candidates" with
                    | Some cs when cs.ValueKind = JsonValueKind.Array ->
                        for c in cs.EnumerateArray() do
                            match ProviderJson.prop c "content" with
                            | Some content ->
                                match ProviderJson.prop content "parts" with
                                | Some ps when ps.ValueKind = JsonValueKind.Array ->
                                    for p in ps.EnumerateArray() do
                                        streamParts.Add(p.Clone())
                                        let thought = match ProviderJson.prop p "thought" with Some x when x.ValueKind = JsonValueKind.True -> true | _ -> false
                                        match ProviderJson.prop p "text" with
                                        | Some t when t.ValueKind = JsonValueKind.String && not thought -> events.Add(TextDelta(t.GetString()))
                                        | _ -> ()
                                        match ProviderJson.prop p "functionCall" with
                                        | Some fc ->
                                            sawFunctionCall <- true
                                            let name = ProviderJson.str fc "name" ""
                                            let id = ProviderJson.str fc "id" ("gemini_synth_stream_" + string index)
                                            let args = match ProviderJson.prop fc "args" with Some a -> a.GetRawText() | _ -> "{}"
                                            events.Add(ToolCallDelta(index, id, name, args))
                                            index <- index + 1
                                        | _ -> ()
                                | _ -> ()
                            | _ -> ()
                            match ProviderJson.prop c "finishReason" with
                            | Some fr when fr.ValueKind = JsonValueKind.String && fr.GetString() <> "" ->
                                finishReason <- ProviderJson.finish(fr.GetString())
                                if sawFunctionCall && finishReason = FinishReason.Stop then finishReason <- FinishReason.ToolCalls
                                terminal <- true
                            | _ -> ()
                    | _ -> ()
                    match ProviderJson.prop root "usageMetadata" with
                    | Some u ->
                        let i = ProviderJson.int64 u "promptTokenCount"
                        let o = ProviderJson.int64 u "candidatesTokenCount"
                        let t = ProviderJson.int64 u "totalTokenCount"
                        usage <- Usage(i, o, (if t = 0L then i + o else t))
                        gotUsage <- true
                        events.Add(UsageUpdate usage)
                    | _ -> ()
                    ProviderJson.events events
            with :? AiException -> reraise() | ex -> raise (ProviderJson.parseError "Failed to decode Gemini stream chunk: " ex)
        let frame = SseFrameParser(cb)
        { new IChatStreamParser with
            member _.PushLine(line) = frame.PushLine(line)
            member _.Finish() =
                let events = ResizeArray<ChatStreamEvent>(frame.Finish())
                if not terminal then raise (AiException(AiError.Decode "Gemini stream ended before a candidate finishReason."))
                if not gotUsage then events.Add(UsageUpdate usage)
                if streamParts.Count > 0 then events.Add(ProviderStateUpdate(providerState streamParts model))
                if not completionEmitted then
                    events.Add(Completed(finishReason, usage))
                    completionEmitted <- true
                ProviderJson.events events }

    member this.SerializeRequest(request: ChatRequest) = this.Serialize(request)
    member this.ParseResponseBody(body: string) = this.ParseResponse(body)
    member this.MakeStreamParser() = this.CreateStreamParser()

type internal GeminiBackendBase(model: string, accountId: string, credentials: ICredentialProvider, options: ProviderOptions, vertex: bool) =
    let codec = GeminiCodec(model, "gemini")
    let credentials = if isNull credentials && not (isNull options) then options.Credentials else credentials
    let baseUrl = if not (isNull options) && not (String.IsNullOrWhiteSpace options.BaseUrl) then options.BaseUrl.TrimEnd('/') else null
    let url (requestModel: string) (stream: bool) =
        let suffix = if stream then ":streamGenerateContent?alt=sse" else ":generateContent"
        if vertex then
            let project = if not (isNull options) && not (String.IsNullOrWhiteSpace options.ProjectId) then options.ProjectId else Environment.GetEnvironmentVariable("GOOGLE_VERTEX_PROJECT")
            let location = if not (isNull options) && not (String.IsNullOrWhiteSpace options.Location) then options.Location else Environment.GetEnvironmentVariable("GOOGLE_VERTEX_LOCATION")
            if String.IsNullOrWhiteSpace project || String.IsNullOrWhiteSpace location then raise (AiException("Vertex Gemini requires ProviderOptions.ProjectId and Location or GOOGLE_VERTEX_PROJECT and GOOGLE_VERTEX_LOCATION."))
            let host = if location = "global" then "aiplatform.googleapis.com" else location + "-aiplatform.googleapis.com"
            let root = if isNull baseUrl then "https://" + host else baseUrl
            root.TrimEnd('/') + $"/v1/projects/{Uri.EscapeDataString(project)}/locations/{Uri.EscapeDataString(location)}/publishers/google/models/{Uri.EscapeDataString(requestModel)}" + suffix
        else
            let modelRoot =
                if isNull baseUrl then "https://generativelanguage.googleapis.com/v1beta/models"
                elif baseUrl.EndsWith("/models", StringComparison.OrdinalIgnoreCase) then baseUrl
                elif baseUrl.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase) then baseUrl + "/models"
                else baseUrl + "/v1beta/models"
            modelRoot + "/" + Uri.EscapeDataString(requestModel) + suffix
    interface IChatBackend with
        member _.PrepareRequest(request, stream, ct) = task {
            let requestModel = if String.IsNullOrWhiteSpace request.Model then model else request.Model
            let requestCodec = GeminiCodec(requestModel, "gemini")
            let headers = Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            headers["Content-Type"] <- "application/json"
            if not (isNull credentials) then
                let! supplied = credentials.GetHeaders(accountId, ct)
                if not (isNull supplied) then
                    for KeyValue(k, v) in supplied do headers[k] <- v
            if not vertex && not (headers.ContainsKey("x-goog-api-key")) then
                match Environment.GetEnvironmentVariable("GEMINI_API_KEY") with null | "" -> () | key -> headers["x-goog-api-key"] <- key
            return PreparedChatRequest(url requestModel stream, headers :> IReadOnlyDictionary<string,string>, requestCodec.Serialize(request))
        }
        member _.ParseResponse(body) = codec.ParseResponse(body)
        member _.CreateStreamParser() = codec.MakeStreamParser()

[<Sealed>]
type GeminiBackend(model: string, accountId: string, credentials: ICredentialProvider, options: ProviderOptions) =
    let inner = GeminiBackendBase(model, accountId, credentials, options, false)
    interface IChatBackend with
        member _.PrepareRequest(r, s, ct) = (inner :> IChatBackend).PrepareRequest(r, s, ct)
        member _.ParseResponse(b) = (inner :> IChatBackend).ParseResponse(b)
        member _.CreateStreamParser() = (inner :> IChatBackend).CreateStreamParser()
    interface IRequestScopedBackend with
        member this.ResolveBackend(requestModel) =
            if String.Equals(model, requestModel, StringComparison.Ordinal) then this :> IChatBackend
            else GeminiBackend(requestModel, accountId, credentials, options) :> IChatBackend

[<Sealed>]
type VertexGeminiBackend(model: string, accountId: string, credentials: ICredentialProvider, options: ProviderOptions) =
    let inner = GeminiBackendBase(model, accountId, credentials, options, true)
    interface IChatBackend with
        member _.PrepareRequest(r, s, ct) = (inner :> IChatBackend).PrepareRequest(r, s, ct)
        member _.ParseResponse(b) = (inner :> IChatBackend).ParseResponse(b)
        member _.CreateStreamParser() = (inner :> IChatBackend).CreateStreamParser()
    interface IRequestScopedBackend with
        member this.ResolveBackend(requestModel) =
            if String.Equals(model, requestModel, StringComparison.Ordinal) then this :> IChatBackend
            else VertexGeminiBackend(requestModel, accountId, credentials, options) :> IChatBackend
