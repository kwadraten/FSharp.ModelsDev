namespace FSharp.ModelsDev.Providers

open System
open System.Collections.Generic
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog

type private KimiCodeCredentialProvider(accountId: string, credentials: ICredentialProvider, options: ProviderOptions, deviceId: string) =
    let optionHeaders = if isNull options || isNull options.Headers then Array.empty else options.Headers |> Seq.toArray

    interface ICredentialProvider with
        member _.GetHeaders(requestedAccount, ct) = task {
            if not (String.Equals(accountId, requestedAccount, StringComparison.Ordinal)) then
                raise (AiException("Kimi Code credentials were requested for a different account."))
            let result = Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            if not (isNull credentials) then
                let! headers = credentials.GetHeaders(accountId, ct)
                if not (isNull headers) then
                    for KeyValue(key,value) in headers do
                        if not (String.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase)) then result[key] <- value
            let mutable selectedDeviceId = deviceId
            match credentials with
            | :? IProviderAuthSessionProvider as sessionProvider ->
                let! session = sessionProvider.GetSessionAsync(accountId, ct)
                if not (isNull session) && not (String.IsNullOrWhiteSpace session.DeviceId) then selectedDeviceId <- session.DeviceId
            | _ -> ()
            for KeyValue(key,value) in KimiCodeHeaders.create selectedDeviceId "1.49.0" do result[key] <- value
            for KeyValue(key,value) in optionHeaders do
                if not (String.Equals(key, "Authorization", StringComparison.OrdinalIgnoreCase))
                   && not (String.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase)) then result[key] <- value
            return result :> IReadOnlyDictionary<string,string>
        }

/// Kimi Code Messages backend. Set ProtocolId to "openai-chat" for the legacy endpoint.
[<Sealed>]
type KimiCodeBackend(model: string, accountId: string, credentials: ICredentialProvider, options: ProviderOptions) =
    let options = if isNull options then ProviderOptions() else options
    let descriptor = options.DiscoveredModel
    let catalogModel =
        if isNull descriptor || not (String.Equals(descriptor.ModelId, model, StringComparison.Ordinal)) then null
        else descriptor
    let modelId = if String.IsNullOrWhiteSpace model && not (isNull catalogModel) then catalogModel.ModelId else model
    let deviceId = Guid.NewGuid().ToString("N")
    let baseUrl =
        if not (String.IsNullOrWhiteSpace options.BaseUrl) then options.BaseUrl.TrimEnd('/')
        elif not (isNull catalogModel) && not (String.IsNullOrWhiteSpace catalogModel.BaseUrl) then catalogModel.BaseUrl.TrimEnd('/')
        else "https://api.kimi.com/coding/v1"
    let innerCredentials = KimiCodeCredentialProvider(accountId, credentials, options, deviceId) :> ICredentialProvider
    let useOpenAiChat =
        String.Equals(options.ProtocolId, "openai-chat", StringComparison.OrdinalIgnoreCase)
        || String.Equals(options.ProtocolId, "openai-compatible", StringComparison.OrdinalIgnoreCase)
    let legacyOptions = ProviderOptions()
    do
        legacyOptions.ProtocolId <- "openai-chat"
        legacyOptions.BaseUrl <- baseUrl
    let legacy = if useOpenAiChat then Some(OpenAIChatBackend(modelId, accountId, innerCredentials, legacyOptions) :> IChatBackend) else None
    let codec = AnthropicCodec(modelId, "kimi-code", false)

    let copyRequest (request: ChatRequest) =
        let copy = ChatRequest(request.Model, request.Messages)
        copy.Temperature <- request.Temperature
        copy.MaxTokens <- request.MaxTokens
        copy.TopP <- request.TopP
        copy.Stream <- request.Stream
        copy.Tools <- request.Tools
        copy.ReasoningEffort <- null
        copy.ReasoningBudgetTokens <- Nullable()
        copy.StopSequences <- request.StopSequences
        copy

    let reasoningFields (request: ChatRequest) =
        let supplied = request.ReasoningEffort
        if request.ReasoningBudgetTokens.HasValue then
            raise (AiException("Kimi Code accepts discovered thinking and effort controls, not reasoning token budgets."))
        if String.IsNullOrWhiteSpace supplied then
            if not (isNull catalogModel) && catalogModel.AlwaysThinking then Some("enabled", null)
            else None
        else
            if isNull catalogModel || isNull catalogModel.ModelInfo then
                raise (AiException("Kimi Code reasoning controls require an authenticated discovered model descriptor."))
            let efforts = if isNull catalogModel.ReasoningEfforts then Array.empty else catalogModel.ReasoningEfforts |> Seq.toArray
            match efforts |> Array.tryFind (fun effort -> String.Equals(effort, supplied, StringComparison.Ordinal)) with
            | Some exact when catalogModel.ModelInfo.SupportsReasoning -> Some("enabled", exact)
            | _ when catalogModel.SupportsThinkingToggle && (supplied.Equals("on", StringComparison.OrdinalIgnoreCase) || supplied.Equals("enabled", StringComparison.OrdinalIgnoreCase)) -> Some("enabled", null)
            | _ when catalogModel.SupportsThinkingToggle && (supplied.Equals("off", StringComparison.OrdinalIgnoreCase) || supplied.Equals("disabled", StringComparison.OrdinalIgnoreCase)) -> Some("disabled", null)
            | _ -> raise (AiException($"Reasoning effort '{supplied}' is not supported by the discovered Kimi Code model '{catalogModel.ModelId}'."))

    let adaptBody (body: string) (request: ChatRequest) =
        let root: JsonObject = JsonNode.Parse(body).AsObject()
        root.Remove("reasoning_effort") |> ignore
        root.Remove("reasoning_budget_tokens") |> ignore
        match reasoningFields request with
        | Some(thinkingType, effort) ->
            let thinking = JsonObject()
            thinking["type"] <- JsonValue.Create(thinkingType)
            root["thinking"] <- thinking
            if not (isNull effort) then root["reasoning_effort"] <- JsonValue.Create(effort)
        | None -> ()
        if not (String.IsNullOrWhiteSpace options.SessionId) then
            root["prompt_cache_key"] <- JsonValue.Create(options.SessionId)
        root.ToJsonString()

    let messagesEndpoint stream =
        let suffix = "/messages"
        if baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) then baseUrl + suffix
        else baseUrl + "/v1" + suffix

    interface IChatBackend with
        member _.PrepareRequest(request, stream, ct) = task {
            match legacy with
            | Some backend ->
                let! prepared = backend.PrepareRequest(request, stream, ct)
                let body = adaptBody prepared.Body request
                return PreparedChatRequest(prepared.Url, prepared.Headers, body)
            | None ->
                let preparedRequest = copyRequest request
                let! supplied = innerCredentials.GetHeaders(accountId, ct)
                let headers = Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
                headers["Content-Type"] <- "application/json"
                headers["anthropic-version"] <- "2023-06-01"
                if not (isNull supplied) then
                    for KeyValue(key, value) in supplied do headers[key] <- value
                let body = JsonNode.Parse(codec.SerializeRequest(preparedRequest)).AsObject()
                body.Remove("reasoning_effort") |> ignore
                body.Remove("reasoning_budget_tokens") |> ignore
                match reasoningFields request with
                | Some(thinkingType, effort) ->
                    let thinking = JsonObject()
                    thinking["type"] <- JsonValue.Create(thinkingType)
                    body["thinking"] <- thinking
                    if not (isNull effort) then body["reasoning_effort"] <- JsonValue.Create(effort)
                | None -> ()
                if not (String.IsNullOrWhiteSpace options.SessionId) then body["prompt_cache_key"] <- JsonValue.Create(options.SessionId)
                if stream then body["stream"] <- JsonValue.Create(true)
                return PreparedChatRequest(messagesEndpoint stream, headers :> IReadOnlyDictionary<string,string>, body.ToJsonString())
        }
        member _.ParseResponse(body) =
            match legacy with Some backend -> backend.ParseResponse(body) | None -> codec.ParseResponseBody(body)
        member _.CreateStreamParser() =
            match legacy with Some backend -> backend.CreateStreamParser() | None -> codec.MakeStreamParser()
    interface IRequestScopedBackend with
        member _.ResolveBackend(requestedModel) =
            let scopedOptions = ProviderOptions()
            scopedOptions.ProtocolId <- options.ProtocolId
            scopedOptions.BaseUrl <- options.BaseUrl
            scopedOptions.ProjectId <- options.ProjectId
            scopedOptions.Location <- options.Location
            scopedOptions.Headers <- options.Headers
            scopedOptions.SessionId <- options.SessionId
            if not (isNull descriptor) && String.Equals(descriptor.ModelId, requestedModel, StringComparison.Ordinal) then
                scopedOptions.DiscoveredModel <- descriptor
            KimiCodeBackend(requestedModel, accountId, credentials, scopedOptions) :> IChatBackend
