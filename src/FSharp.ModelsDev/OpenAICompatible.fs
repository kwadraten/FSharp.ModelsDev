namespace FSharp.ModelsDev.Providers

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json

open FSharp.ModelsDev

/// OpenAI Chat Completions wire protocol; also covers OpenAI-compatible endpoints.
[<Sealed>]
type OpenAICompatibleChatProtocol() =

    static let roleToWire (role: Role) =
        match role with
        | Role.System -> "system"
        | Role.User -> "user"
        | Role.Tool -> "tool"
        | _ -> "assistant"

    static let tryGetProperty (el: JsonElement) (name: string) : JsonElement option =
        if el.ValueKind = JsonValueKind.Object then
            match el.TryGetProperty(name) with
            | true, p -> Some p
            | _ -> None
        else
            None

    static let getString (el: JsonElement) (name: string) (fallback: string) : string =
        match tryGetProperty el name with
        | Some p when p.ValueKind = JsonValueKind.String ->
            match p.GetString() with
            | null -> fallback
            | s -> s
        | _ -> fallback

    static let getInt64 (el: JsonElement) (name: string) : int64 =
        match tryGetProperty el name with
        | Some p when p.ValueKind = JsonValueKind.Number ->
            match p.TryGetInt64() with
            | true, v -> v
            | _ -> 0L
        | _ -> 0L

    static let mapFinishReason (value: string) : FinishReason =
        match value with
        | null -> FinishReason.Unknown
        | v when v.Equals("stop", StringComparison.OrdinalIgnoreCase) -> FinishReason.Stop
        | v when v.Equals("length", StringComparison.OrdinalIgnoreCase) -> FinishReason.Length
        | v when v.Equals("tool_calls", StringComparison.OrdinalIgnoreCase) -> FinishReason.ToolCalls
        | v when v.Equals("content_filter", StringComparison.OrdinalIgnoreCase) -> FinishReason.ContentFilter
        | _ -> FinishReason.Unknown

    static let parseRole (value: string) : Role =
        match value with
        | "system" -> Role.System
        | "user" -> Role.User
        | "tool" -> Role.Tool
        | _ -> Role.Assistant

    static let parseUsageElement (el: JsonElement) : Usage =
        if el.ValueKind = JsonValueKind.Object then
            Usage(getInt64 el "prompt_tokens", getInt64 el "completion_tokens", getInt64 el "total_tokens")
        else
            Usage()

    static let writeContent (writer: Utf8JsonWriter) (parts: IReadOnlyList<ContentPart>) =
        if isNull parts || parts.Count = 0 then
            writer.WriteNullValue()
        elif parts |> Seq.exists (fun p -> not (p :? TextPart)) then
            writer.WriteStartArray()
            for part in parts do
                match part with
                | :? TextPart as t ->
                    writer.WriteStartObject()
                    writer.WriteString("type", "text")
                    writer.WriteString("text", t.Text)
                    writer.WriteEndObject()
                | :? ImagePart as img ->
                    writer.WriteStartObject()
                    writer.WriteString("type", "image_url")
                    writer.WritePropertyName("image_url")
                    writer.WriteStartObject()
                    writer.WriteString("url", img.Url)
                    writer.WriteEndObject()
                    writer.WriteEndObject()
                | _ -> ()
            writer.WriteEndArray()
        else
            let text =
                parts
                |> Seq.choose (function
                    | :? TextPart as t -> Some t.Text
                    | _ -> None)
                |> String.concat ""

            writer.WriteStringValue(text)

    static let writeMessage (writer: Utf8JsonWriter) (message: ChatMessage) =
        writer.WriteStartObject()
        writer.WriteString("role", roleToWire message.Role)
        writer.WritePropertyName("content")
        writeContent writer message.Parts

        if not (String.IsNullOrEmpty message.Name) then
            writer.WriteString("name", message.Name)

        if not (isNull message.ToolCalls) && message.ToolCalls.Count > 0 then
            writer.WritePropertyName("tool_calls")
            writer.WriteStartArray()
            for call in message.ToolCalls do
                writer.WriteStartObject()
                writer.WriteString("id", call.Id)
                writer.WriteString("type", "function")
                writer.WritePropertyName("function")
                writer.WriteStartObject()
                writer.WriteString("name", call.Name)
                writer.WriteString("arguments", call.ArgumentsJson)
                writer.WriteEndObject()
                writer.WriteEndObject()
            writer.WriteEndArray()

        if not (String.IsNullOrEmpty message.ToolCallId) then
            writer.WriteString("tool_call_id", message.ToolCallId)

        writer.WriteEndObject()

    static let serializeRequest (request: ChatRequest) (stream: bool) : string =
        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteString("model", request.Model)
        writer.WritePropertyName("messages")
        writer.WriteStartArray()

        if not (isNull request.Messages) then
            for message in request.Messages do
                writeMessage writer message

        writer.WriteEndArray()

        if request.Temperature.HasValue then
            writer.WriteNumber("temperature", request.Temperature.Value)

        if request.TopP.HasValue then
            writer.WriteNumber("top_p", request.TopP.Value)

        if request.MaxTokens.HasValue then
            writer.WriteNumber("max_tokens", request.MaxTokens.Value)

        if not (isNull request.StopSequences) && request.StopSequences.Count > 0 then
            writer.WritePropertyName("stop")
            writer.WriteStartArray()
            for stop in request.StopSequences do
                if isNull stop then
                    writer.WriteNullValue()
                else
                    writer.WriteStringValue(stop)
            writer.WriteEndArray()

        if not (isNull request.Tools) && request.Tools.Count > 0 then
            writer.WritePropertyName("tools")
            writer.WriteStartArray()

            for tool in request.Tools do
                writer.WriteStartObject()
                writer.WriteString("type", "function")
                writer.WritePropertyName("function")
                writer.WriteStartObject()
                writer.WriteString("name", tool.Name)
                writer.WriteString("description", tool.Description)
                writer.WritePropertyName("parameters")

                if String.IsNullOrWhiteSpace tool.ParametersJson then
                    writer.WriteNullValue()
                else
                    try
                        use doc = JsonDocument.Parse(tool.ParametersJson)
                        doc.RootElement.WriteTo(writer)
                    with :? JsonException as ex ->
                        raise (AiException(AiError.Decode $"Tool '{tool.Name}' has invalid parameters JSON.", ex))

                writer.WriteEndObject()
                writer.WriteEndObject()

            writer.WriteEndArray()

        if stream then
            writer.WriteBoolean("stream", true)
            writer.WritePropertyName("stream_options")
            writer.WriteStartObject()
            writer.WriteBoolean("include_usage", true)
            writer.WriteEndObject()

        if not (String.IsNullOrEmpty request.ReasoningEffort) then
            writer.WriteString("reasoning_effort", request.ReasoningEffort)

        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(buffer.ToArray())

    static let parseContentParts (el: JsonElement) : IReadOnlyList<ContentPart> =
        let parts = ResizeArray<ContentPart>()

        match el.ValueKind with
        | JsonValueKind.String ->
            let s = el.GetString()
            parts.Add(TextPart(if isNull s then "" else s))
        | JsonValueKind.Array ->
            for item in el.EnumerateArray() do
                match tryGetProperty item "type" with
                | Some t when t.ValueKind = JsonValueKind.String ->
                    match t.GetString() with
                    | "text" -> parts.Add(TextPart(getString item "text" ""))
                    | "image_url" ->
                        match tryGetProperty item "image_url" with
                        | Some img -> parts.Add(ImagePart(getString img "url" ""))
                        | None -> ()
                    | _ -> ()
                | _ -> ()
        | _ -> ()

        parts :> IReadOnlyList<ContentPart>

    static let parseMessageElement (el: JsonElement) : ChatMessage =
        let role = parseRole (getString el "role" "assistant")

        let parts =
            match tryGetProperty el "content" with
            | Some c -> parseContentParts c
            | None -> ResizeArray<ContentPart>() :> IReadOnlyList<ContentPart>

        let toolCalls =
            match tryGetProperty el "tool_calls" with
            | Some tcs when tcs.ValueKind = JsonValueKind.Array ->
                let calls = ResizeArray<ToolCall>()

                for item in tcs.EnumerateArray() do
                    match tryGetProperty item "function" with
                    | Some fn when fn.ValueKind = JsonValueKind.Object ->
                        calls.Add(ToolCall(getString item "id" "", getString fn "name" "", getString fn "arguments" ""))
                    | _ -> ()

                calls :> IReadOnlyList<ToolCall>
            | _ -> null

        let toolCallId =
            match tryGetProperty el "tool_call_id" with
            | Some p when p.ValueKind = JsonValueKind.String -> p.GetString()
            | _ -> null

        ChatMessage(role, parts, null, toolCalls, toolCallId)

    static let parseResponse (body: string) : ChatResponse =
        try
            use doc = JsonDocument.Parse(body)
            let root = doc.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                raise (AiException(AiError.Decode "Chat completion body is not a JSON object."))

            let id = getString root "id" ""
            let model = getString root "model" ""

            let usage =
                match tryGetProperty root "usage" with
                | Some u when u.ValueKind = JsonValueKind.Object -> parseUsageElement u
                | _ -> Usage()

            let mutable finishReason = FinishReason.Unknown
            let mutable message: ChatMessage = null

            match tryGetProperty root "choices" with
            | Some choices when choices.ValueKind = JsonValueKind.Array && choices.GetArrayLength() > 0 ->
                let choice = choices[0]
                finishReason <- mapFinishReason (getString choice "finish_reason" null)

                match tryGetProperty choice "message" with
                | Some m when m.ValueKind = JsonValueKind.Object -> message <- parseMessageElement m
                | _ -> ()
            | _ -> ()

            if isNull message then
                message <- ChatMessage.Assistant ""

            ChatResponse(id, model, message, usage, finishReason)
        with
        | :? AiException -> reraise()
        | ex -> raise (AiException(AiError.Decode($"Failed to parse chat completion: {ex.Message}"), ex))

    static let parseChunkEvent (json: string) : ChatStreamEvent =
        try
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement

            let mutable textDelta: string = null
            let mutable firstToolDelta: ToolCallDelta = null
            let mutable usage: Usage = null
            let mutable finishReason = FinishReason.Unknown
            let mutable hasFinishReason = false

            match tryGetProperty root "choices" with
            | Some choices when choices.ValueKind = JsonValueKind.Array ->
                for choice in choices.EnumerateArray() do
                    match tryGetProperty choice "delta" with
                    | Some delta when delta.ValueKind = JsonValueKind.Object ->
                        match tryGetProperty delta "content" with
                        | Some c when c.ValueKind = JsonValueKind.String ->
                            let s = c.GetString()
                            if not (String.IsNullOrEmpty s) then textDelta <- s
                        | Some c when c.ValueKind = JsonValueKind.Array ->
                            let sb = StringBuilder()

                            for item in c.EnumerateArray() do
                                match tryGetProperty item "type" with
                                | Some t when t.ValueKind = JsonValueKind.String && t.GetString() = "text" ->
                                    sb.Append(getString item "text" "") |> ignore
                                | _ -> ()

                            if sb.Length > 0 then textDelta <- sb.ToString()
                        | _ -> ()

                        match tryGetProperty delta "tool_calls" with
                        | Some tcs when tcs.ValueKind = JsonValueKind.Array ->
                            let mutable fallbackIndex = 0

                            for tc in tcs.EnumerateArray() do
                                let index =
                                    match tryGetProperty tc "index" with
                                    | Some idx when idx.ValueKind = JsonValueKind.Number ->
                                        match idx.TryGetInt32() with
                                        | true, v -> v
                                        | _ -> fallbackIndex
                                    | _ -> fallbackIndex

                                let id = getString tc "id" null

                                let name, arguments =
                                    match tryGetProperty tc "function" with
                                    | Some fn when fn.ValueKind = JsonValueKind.Object ->
                                        getString fn "name" null, getString fn "arguments" null
                                    | _ -> null, null

                                let toolDelta = ToolCallDelta(index, id, name, arguments)
                                if isNull firstToolDelta then firstToolDelta <- toolDelta
                                fallbackIndex <- fallbackIndex + 1
                        | _ -> ()
                    | _ -> ()

                    match tryGetProperty choice "finish_reason" with
                    | Some fr when fr.ValueKind = JsonValueKind.String ->
                        match fr.GetString() with
                        | null -> ()
                        | s ->
                            hasFinishReason <- true
                            finishReason <- mapFinishReason s
                    | _ -> ()
            | _ -> ()

            match tryGetProperty root "usage" with
            | Some u when u.ValueKind = JsonValueKind.Object ->
                let mutable en = u.EnumerateObject()
                if en.MoveNext() then
                    usage <- parseUsageElement u
            | _ -> ()

            if not (String.IsNullOrEmpty textDelta) then
                TextDelta textDelta :> ChatStreamEvent
            elif not (isNull firstToolDelta) then
                firstToolDelta :> ChatStreamEvent
            elif hasFinishReason then
                Completed(finishReason, (if isNull usage then Usage() else usage)) :> ChatStreamEvent
            elif not (isNull usage) then
                UsageUpdate usage :> ChatStreamEvent
            else
                null
        with
        | :? AiException -> reraise()
        | ex -> raise (AiException(AiError.Decode($"Failed to parse stream chunk: {ex.Message}"), ex))

    static let tryParseStreamLine (line: string) : ChatStreamEvent =
        if String.IsNullOrWhiteSpace line then
            null
        else
            let trimmed = line.TrimStart()

            if trimmed.StartsWith(":", StringComparison.Ordinal) then
                null
            elif trimmed.StartsWith("event:", StringComparison.Ordinal) then
                null
            elif trimmed.StartsWith("data:", StringComparison.Ordinal) then
                let payload = trimmed.Substring("data:".Length).TrimStart()

                if payload.Length = 0 || payload = "[DONE]" then
                    null
                else
                    parseChunkEvent payload
            else
                null

    /// Serializes a request to the OpenAI chat completions wire shape.
    member _.SerializeRequest(request: ChatRequest, stream: bool) : string =
        serializeRequest request stream

    /// Parses a non-streaming chat completion body.
    member _.ParseResponse(body: string) : ChatResponse =
        parseResponse body

    /// Maps one raw SSE line to a stream event, or null when the line carries no event.
    member _.TryParseStreamLine(line: string) : ChatStreamEvent =
        tryParseStreamLine line

    interface IChatProtocol with
        member this.SerializeRequest(request, stream) = this.SerializeRequest(request, stream)
        member this.ParseResponse(body) = this.ParseResponse(body)
        member this.TryParseStreamLine(line) = this.TryParseStreamLine(line)
