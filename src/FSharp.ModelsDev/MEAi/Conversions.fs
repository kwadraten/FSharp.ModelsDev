namespace FSharp.ModelsDev.MEAi

open System
open System.Collections.Generic
open System.Text
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

    let private parseCompleteArguments (json: string) : IDictionary<string, obj> =
        try
            use document = JsonDocument.Parse json
            if document.RootElement.ValueKind <> JsonValueKind.Object then
                raise (AiException(AiError.Decode "Completed streamed tool arguments must be a JSON object."))
            let arguments = Dictionary<string, obj>()
            for property in document.RootElement.EnumerateObject() do
                arguments[property.Name] <- property.Value.Deserialize<obj>()
            arguments :> IDictionary<string, obj>
        with
        | :? AiException -> reraise()
        | :? JsonException as ex -> raise (AiException(AiError.Decode $"Completed streamed tool arguments are invalid JSON: {ex.Message}", ex))

    let private serializeArguments (arguments: IDictionary<string, obj>) : string =
        if isNull arguments then "{}" else JsonSerializer.Serialize arguments

    let private resultToText (result: obj) : string =
        match result with
        | :? string as text -> text
        | null -> null
        | other -> JsonSerializer.Serialize other

    [<Literal>]
    let providerStateKey = "FSharp.ModelsDev.ProviderState"
    [<Literal>]
    let reasoningEffortKey = "FSharp.ModelsDev.ReasoningEffort"
    [<Literal>]
    let reasoningBudgetTokensKey = "FSharp.ModelsDev.ReasoningBudgetTokens"

    let private ensureProperties (properties: Microsoft.Extensions.AI.AdditionalPropertiesDictionary) =
        if isNull properties then Microsoft.Extensions.AI.AdditionalPropertiesDictionary()
        else properties

    let private providerState (properties: IDictionary<string, obj>) =
        if isNull properties then null
        else
            match properties.TryGetValue(providerStateKey) with
            | true, (:? string as state) -> state
            | true, (:? JsonElement as state) when state.ValueKind = JsonValueKind.String -> state.GetString()
            | true, state when not (isNull state) -> JsonSerializer.Serialize state
            | _ -> null

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
                    contents.Add(AiFunctionCallContent(call.Id, call.Name, parseCompleteArguments call.ArgumentsJson))
        let converted = AiMessage(roleToAi message.Role, contents)
        if not (String.IsNullOrEmpty message.Name) then
            converted.AuthorName <- message.Name
        if not (String.IsNullOrEmpty message.ProviderStateJson) then
            let properties = ensureProperties converted.AdditionalProperties
            properties[providerStateKey] <- box message.ProviderStateJson
            converted.AdditionalProperties <- properties
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
        let converted =
            if toolCalls.Count > 0 then ChatMessage(roleFromAi message.Role, parts, name, toolCalls, null)
            elif not (isNull toolCallId) then ChatMessage(roleFromAi message.Role, parts, name, null, toolCallId)
            else ChatMessage(roleFromAi message.Role, parts, name, null, null)
        converted.ProviderStateJson <- providerState message.AdditionalProperties
        converted

    let requestFromAi
        (defaultModel: string)
        (messages: IEnumerable<AiMessage>)
        (options: AiChatOptions)
        (stream: bool)
        : ChatRequest =
        let modelId =
            if isNull options || String.IsNullOrEmpty options.ModelId then defaultModel
            else options.ModelId
        let requestMessages =
            messages
            |> Seq.collect (fun message ->
                let results =
                    if isNull message.Contents then Array.empty
                    else
                        message.Contents
                        |> Seq.choose (function
                            | :? AiFunctionResultContent as result ->
                                Some(ChatMessage.ToolResult(result.CallId, resultToText result.Result))
                            | _ -> None)
                        |> Seq.toArray
                if results.Length > 0 then results :> seq<ChatMessage>
                else Seq.singleton (messageFromAi message))
            |> Seq.toArray
        let request = ChatRequest(modelId, requestMessages)
        request.Stream <- stream
        if not (isNull options) then
            if not (isNull options.AdditionalProperties) then
                match options.AdditionalProperties.TryGetValue reasoningEffortKey with
                | true, (:? string as effort) -> request.ReasoningEffort <- effort
                | _ -> ()
                match options.AdditionalProperties.TryGetValue reasoningBudgetTokensKey with
                | true, (:? int as budget) -> request.ReasoningBudgetTokens <- Nullable budget
                | true, (:? JsonElement as budget) when budget.ValueKind = JsonValueKind.Number ->
                    match budget.TryGetInt32() with true, value -> request.ReasoningBudgetTokens <- Nullable value | _ -> ()
                | _ -> ()
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

    /// Creates one converter per stream so function JSON and provider state remain scoped to it.
    let createEventConverter () =
        let calls = Dictionary<int, struct (string * string * StringBuilder)>()
        let mutable providerStateJson: string = null
        let messageId = "fsharp-modelsdev-" + Guid.NewGuid().ToString("N")
        fun (event: ChatStreamEvent) ->
            let updates = ResizeArray<AiResponseUpdate>()
            match event with
            | :? TextDelta as delta ->
                let update = AiResponseUpdate()
                update.Role <- Nullable<_>(AiRole.Assistant)
                update.MessageId <- messageId
                if not (isNull delta.Text) then update.Contents.Add(AiTextContent delta.Text)
                updates.Add(update)
            | :? ToolCallDelta as delta ->
                let key = delta.Index
                let id, name, args =
                    match calls.TryGetValue key with
                    | true, struct (id, name, args) ->
                        (if String.IsNullOrEmpty delta.Id then id else delta.Id),
                        (if String.IsNullOrEmpty delta.Name then name else delta.Name), args
                    | _ -> (if isNull delta.Id then "" else delta.Id), (if isNull delta.Name then "" else delta.Name), StringBuilder()
                if not (String.IsNullOrEmpty delta.ArgumentsDelta) then args.Append(delta.ArgumentsDelta) |> ignore
                calls[key] <- struct (id, name, args)
            | :? ProviderStateUpdate as state ->
                providerStateJson <- state.StateJson
                let update = AiResponseUpdate()
                update.Role <- Nullable<_>(AiRole.Assistant)
                update.MessageId <- messageId
                let properties = ensureProperties update.AdditionalProperties
                properties[providerStateKey] <- box state.StateJson
                update.AdditionalProperties <- properties
                updates.Add(update)
            | :? UsageUpdate -> () // Completed carries the final usage once.
            | :? Completed as completed ->
                let update = AiResponseUpdate()
                update.MessageId <- messageId
                update.FinishReason <- finishToAi completed.FinishReason
                if not (isNull completed.Usage) then update.Contents.Add(AiUsageContent(usageToAi completed.Usage))
                for KeyValue(_, struct (id, name, args)) in calls do
                    let parsed = parseCompleteArguments (args.ToString())
                    update.Contents.Add(AiFunctionCallContent(id, name, parsed))
                if not (String.IsNullOrEmpty providerStateJson) then
                    let properties = ensureProperties update.AdditionalProperties
                    properties[providerStateKey] <- box providerStateJson
                    update.AdditionalProperties <- properties
                updates.Add(update)
            | _ -> ()
            updates.ToArray()

/// Adapts an IAsyncEnumerable with a mapping function so it can be consumed by C# await foreach.
module internal AsyncEnumerableBridge =

    let private linkedToken (callersToken: CancellationToken) (sourceToken: CancellationToken) =
        if callersToken.CanBeCanceled && sourceToken.CanBeCanceled && callersToken <> sourceToken then
            let linked = CancellationTokenSource.CreateLinkedTokenSource(callersToken, sourceToken)
            linked.Token, Some linked
        elif callersToken.CanBeCanceled then callersToken, None
        else sourceToken, None

    type private MappingEnumerator<'T, 'U>(source: IAsyncEnumerator<'T>, mapping: 'T -> 'U, linked: CancellationTokenSource option) =
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
                match linked with
                | None -> source.DisposeAsync()
                | Some linkedSource ->
                    let cleanup = task {
                        let! error = task {
                            try
                                do! source.DisposeAsync().AsTask()
                                return None
                            with ex -> return Some ex
                        }
                        linkedSource.Dispose()
                        match error with Some ex -> return raise ex | None -> return ()
                    }
                    ValueTask(cleanup :> Task)

    type private MappingEnumerable<'T, 'U>(source: IAsyncEnumerable<'T>, mapping: 'T -> 'U, cancellationToken: CancellationToken) =
        interface IAsyncEnumerable<'U> with
            member _.GetAsyncEnumerator(callersToken) =
                let effective, linked = linkedToken callersToken cancellationToken
                let enumerator = source.GetAsyncEnumerator(effective)
                (MappingEnumerator(enumerator, mapping, linked) :> IAsyncEnumerator<'U>)

    type private FlatMappingEnumerator<'T, 'U>(source: IAsyncEnumerator<'T>, mapping: 'T -> seq<'U>, linked: CancellationTokenSource option) =
        let mutable current = Unchecked.defaultof<'U>
        let mutable pending = Seq.empty<'U>.GetEnumerator()
        interface IAsyncEnumerator<'U> with
            member _.Current = current
            member _.MoveNextAsync() =
                ValueTask<bool>(task {
                    let mutable found = false
                    let mutable ended = false
                    while not found && not ended do
                        if pending.MoveNext() then
                            current <- pending.Current
                            found <- true
                        else
                            pending.Dispose()
                            let! hasNext = source.MoveNextAsync().AsTask()
                            if hasNext then
                                let values = mapping source.Current |> Seq.toArray
                                pending <- (values :> seq<'U>).GetEnumerator()
                            else ended <- true
                    return found
                })
            member _.DisposeAsync() =
                pending.Dispose()
                match linked with
                | None -> source.DisposeAsync()
                | Some linkedSource ->
                    let cleanup = task {
                        let! error = task {
                            try
                                do! source.DisposeAsync().AsTask()
                                return None
                            with ex -> return Some ex
                        }
                        linkedSource.Dispose()
                        match error with Some ex -> return raise ex | None -> return ()
                    }
                    ValueTask(cleanup :> Task)

    type private FlatMappingEnumerable<'T, 'U>(source: IAsyncEnumerable<'T>, mapping: 'T -> seq<'U>, cancellationToken: CancellationToken) =
        interface IAsyncEnumerable<'U> with
            member _.GetAsyncEnumerator(callersToken) =
                let effective, linked = linkedToken callersToken cancellationToken
                FlatMappingEnumerator(source.GetAsyncEnumerator(effective), mapping, linked) :> IAsyncEnumerator<'U>

    let map (mapping: 'T -> 'U) (cancellationToken: CancellationToken) (source: IAsyncEnumerable<'T>) : IAsyncEnumerable<'U> =
        MappingEnumerable<'T, 'U>(source, mapping, cancellationToken) :> IAsyncEnumerable<'U>

    let mapMany (mapping: 'T -> seq<'U>) (cancellationToken: CancellationToken) (source: IAsyncEnumerable<'T>) : IAsyncEnumerable<'U> =
        FlatMappingEnumerable<'T, 'U>(source, mapping, cancellationToken) :> IAsyncEnumerable<'U>
