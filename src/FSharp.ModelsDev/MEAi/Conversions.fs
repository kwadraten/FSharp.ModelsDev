namespace FSharp.ModelsDev.MEAi

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharp.ModelsDev

/// Bidirectional conversions between FSharp.ModelsDev contract types and Microsoft.Extensions.AI types.
module internal Conversions =

    type AiContent = Microsoft.Extensions.AI.AIContent
    type AiTextContent = Microsoft.Extensions.AI.TextContent
    type AiDataContent = Microsoft.Extensions.AI.DataContent
    type AiFunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent
    type AiFunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent
    type AiUsageContent = Microsoft.Extensions.AI.UsageContent
    type AiMessage = Microsoft.Extensions.AI.ChatMessage
    type AiResponse = Microsoft.Extensions.AI.ChatResponse
    type AiResponseUpdate = Microsoft.Extensions.AI.ChatResponseUpdate
    type AiChatOptions = Microsoft.Extensions.AI.ChatOptions
    type AiRole = Microsoft.Extensions.AI.ChatRole
    type AiFinishReason = Microsoft.Extensions.AI.ChatFinishReason
    type AiUsageDetails = Microsoft.Extensions.AI.UsageDetails
    type AiFunction = Microsoft.Extensions.AI.AIFunction

    let roleToAi (role: Role) : AiRole =
        match role with
        | Role.System -> AiRole.System
        | Role.Assistant -> AiRole.Assistant
        | Role.Tool -> AiRole.Tool
        | _ -> AiRole.User

    let roleFromAi (role: AiRole) : Role =
        match role.Value with
        | "system"
        | "developer" -> Role.System
        | "assistant" -> Role.Assistant
        | "tool" -> Role.Tool
        | _ -> Role.User

    let finishToAi (finishReason: FinishReason) : Nullable<AiFinishReason> =
        match finishReason with
        | FinishReason.Stop -> Nullable<_>(AiFinishReason.Stop)
        | FinishReason.Length -> Nullable<_>(AiFinishReason.Length)
        | FinishReason.ToolCalls -> Nullable<_>(AiFinishReason.ToolCalls)
        | FinishReason.ContentFilter -> Nullable<_>(AiFinishReason.ContentFilter)
        | _ -> Nullable<_>()

    let usageToAi (usage: Usage) : AiUsageDetails =
        let details = AiUsageDetails()
        if not (isNull usage) then
            details.InputTokenCount <- Nullable<_>(usage.InputTokens)
            details.OutputTokenCount <- Nullable<_>(usage.OutputTokens)
            details.TotalTokenCount <- Nullable<_>(usage.TotalTokens)
        details

    let private parseArguments (json: string) : IDictionary<string, obj> =
        let arguments = Dictionary<string, obj>()
        if not (String.IsNullOrWhiteSpace json) then
            (try
                use document = JsonDocument.Parse json
                if document.RootElement.ValueKind = JsonValueKind.Object then
                    for property in document.RootElement.EnumerateObject() do
                        arguments[property.Name] <- property.Value.Deserialize<obj>()
             with :? JsonException ->
                ())
        arguments :> IDictionary<string, obj>

    let private serializeArguments (arguments: IDictionary<string, obj>) : string =
        if isNull arguments then "{}" else JsonSerializer.Serialize arguments

    let private resultToText (result: obj) : string =
        match result with
        | :? string as text -> text
        | null -> null
        | other -> JsonSerializer.Serialize other

    let messageToAi (message: ChatMessage) : AiMessage =
        let contents = ResizeArray<AiContent>()
        if message.Role = Role.Tool && not (String.IsNullOrEmpty message.ToolCallId) then
            contents.Add(AiFunctionResultContent(message.ToolCallId, message.Text))
        else
            if not (isNull message.Parts) then
                for part in message.Parts do
                    match part with
                    | :? TextPart as text -> contents.Add(AiTextContent text.Text)
                    | :? ImagePart as image -> contents.Add(AiDataContent(image.Url, image.MediaType))
                    | _ -> ()
            if not (isNull message.ToolCalls) then
                for call in message.ToolCalls do
                    contents.Add(AiFunctionCallContent(call.Id, call.Name, parseArguments call.ArgumentsJson))
        let converted = AiMessage(roleToAi message.Role, contents)
        if not (String.IsNullOrEmpty message.Name) then
            converted.AuthorName <- message.Name
        converted

    let messageFromAi (message: AiMessage) : ChatMessage =
        let parts = ResizeArray<ContentPart>()
        let toolCalls = ResizeArray<ToolCall>()
        let mutable toolCallId: string = null
        if not (isNull message.Contents) then
            for content in message.Contents do
                match content with
                | :? AiTextContent as text -> parts.Add(TextPart text.Text)
                | :? AiDataContent as data when not (isNull data.Uri) ->
                    parts.Add(ImagePart(data.Uri, mediaType = data.MediaType))
                | :? AiFunctionCallContent as call ->
                    toolCalls.Add(ToolCall(call.CallId, call.Name, serializeArguments call.Arguments))
                | :? AiFunctionResultContent as result ->
                    toolCallId <- result.CallId
                    parts.Add(TextPart(resultToText result.Result))
                | _ -> ()
        let name = if String.IsNullOrEmpty message.AuthorName then null else message.AuthorName
        if toolCalls.Count > 0 then
            ChatMessage(roleFromAi message.Role, parts, name, toolCalls, null)
        elif not (isNull toolCallId) then
            ChatMessage(roleFromAi message.Role, parts, name, null, toolCallId)
        else
            ChatMessage(roleFromAi message.Role, parts, name, null, null)

    let requestFromAi
        (defaultModel: string)
        (messages: IEnumerable<AiMessage>)
        (options: AiChatOptions)
        (stream: bool)
        : ChatRequest =
        let modelId =
            if isNull options || String.IsNullOrEmpty options.ModelId then defaultModel
            else options.ModelId
        let request = ChatRequest(modelId, Seq.map messageFromAi messages |> Seq.toArray)
        request.Stream <- stream
        if not (isNull options) then
            if options.Temperature.HasValue then request.Temperature <- Nullable<_>(float options.Temperature.Value)
            if options.MaxOutputTokens.HasValue then request.MaxTokens <- options.MaxOutputTokens
            if options.TopP.HasValue then request.TopP <- Nullable<_>(float options.TopP.Value)
            if not (isNull options.StopSequences) && options.StopSequences.Count > 0 then
                request.StopSequences <- Seq.toArray options.StopSequences
            if not (isNull options.Tools) && options.Tools.Count > 0 then
                let tools =
                    options.Tools
                    |> Seq.choose (function
                        | :? AiFunction as fn ->
                            let description = if isNull fn.Description then "" else fn.Description
                            let schema =
                                if fn.JsonSchema.ValueKind = JsonValueKind.Undefined then "{}"
                                else fn.JsonSchema.GetRawText()
                            Some(ToolDefinition(fn.Name, description, schema))
                        | _ -> None)
                    |> Seq.toArray
                if tools.Length > 0 then request.Tools <- tools
        request

    let responseToAi (response: ChatResponse) : AiResponse =
        let converted = AiResponse(messageToAi response.Message)
        converted.ResponseId <- response.Id
        converted.ModelId <- response.Model
        converted.FinishReason <- finishToAi response.FinishReason
        if not (isNull response.Usage) then
            converted.Usage <- usageToAi response.Usage
        converted

    let updateFromEvent (event: ChatStreamEvent) : AiResponseUpdate =
        let update = AiResponseUpdate()
        match event with
        | :? TextDelta as delta ->
            update.Role <- Nullable<_>(AiRole.Assistant)
            if not (isNull delta.Text) then
                update.Contents.Add(AiTextContent delta.Text)
        | :? ToolCallDelta as delta ->
            update.Role <- Nullable<_>(AiRole.Assistant)
            update.Contents.Add(AiFunctionCallContent(delta.Id, delta.Name, parseArguments delta.ArgumentsDelta))
        | :? UsageUpdate as usage ->
            update.Contents.Add(AiUsageContent(usageToAi usage.Usage))
        | :? Completed as completed ->
            update.FinishReason <- finishToAi completed.FinishReason
            update.Contents.Add(AiUsageContent(usageToAi completed.Usage))
        | _ -> ()
        update

/// Adapts an IAsyncEnumerable with a mapping function so it can be consumed by C# await foreach.
module internal AsyncEnumerableBridge =

    type private MappingEnumerator<'T, 'U>(source: IAsyncEnumerator<'T>, mapping: 'T -> 'U) =
        let mutable current = Unchecked.defaultof<'U>
        interface IAsyncEnumerator<'U> with
            member _.Current = current
            member _.MoveNextAsync() : ValueTask<bool> =
                let move = source.MoveNextAsync()
                if move.IsCompletedSuccessfully then
                    let hasNext = move.Result
                    if hasNext then
                        current <- mapping source.Current
                    ValueTask<bool>(hasNext)
                else
                    ValueTask<bool>(task {
                        let! hasNext = move.AsTask()
                        if hasNext then
                            current <- mapping source.Current
                        return hasNext
                    })
            member _.DisposeAsync() : ValueTask =
                source.DisposeAsync()

    type private MappingEnumerable<'T, 'U>(source: IAsyncEnumerable<'T>, mapping: 'T -> 'U, cancellationToken: CancellationToken) =
        interface IAsyncEnumerable<'U> with
            member _.GetAsyncEnumerator(callersToken) =
                let effective = if callersToken.CanBeCanceled then callersToken else cancellationToken
                let enumerator = source.GetAsyncEnumerator(effective)
                (MappingEnumerator(enumerator, mapping) :> IAsyncEnumerator<'U>)

    let map (mapping: 'T -> 'U) (cancellationToken: CancellationToken) (source: IAsyncEnumerable<'T>) : IAsyncEnumerable<'U> =
        MappingEnumerable<'T, 'U>(source, mapping, cancellationToken) :> IAsyncEnumerable<'U>
