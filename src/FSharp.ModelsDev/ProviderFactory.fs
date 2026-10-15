namespace FSharp.ModelsDev.Providers

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog

[<AllowNullLiteral>]
type IChatClientFactory =
    abstract CreateClient: providerId: string * accountId: string * modelId: string -> IChatModel

type private ConfiguredBackend(inner: IChatBackend, overrides: IReadOnlyDictionary<string, string>) =
    interface IChatBackend with
        member _.PrepareRequest(request, stream, ct) = task {
            let! prepared = inner.PrepareRequest(request, stream, ct)
            let headers = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            for KeyValue(key, value) in prepared.Headers do headers.[key] <- value
            for KeyValue(key, value) in overrides do headers.[key] <- value
            return PreparedChatRequest(prepared.Url, headers, prepared.Body)
        }
        member _.ParseResponse(body) = inner.ParseResponse(body)
        member _.CreateStreamParser() = inner.CreateStreamParser()
    interface IRequestScopedBackend with
        member _.ResolveBackend(modelId) =
            let scoped =
                match inner with
                | :? IRequestScopedBackend as resolver -> resolver.ResolveBackend(modelId)
                | _ -> inner
            ConfiguredBackend(scoped, overrides) :> IChatBackend
    interface IStreamingCompletionBackend with
        member _.UseStreamingForCompletions =
            match inner with
            | :? IStreamingCompletionBackend as streaming -> streaming.UseStreamingForCompletions
            | _ -> false

/// Claude requires max_tokens; cap only its implicit default using the requested model's catalog entry.
type private CatalogTokenLimitModel(inner: IChatModel, catalog: IModelCatalog, providerId: string) =
    let prepare (request: ChatRequest) =
        let mutable info = Unchecked.defaultof<ModelInfo>
        let modelId = if String.IsNullOrWhiteSpace request.Model then inner.Model else request.Model
        if not request.MaxTokens.HasValue && catalog.TryGet(providerId, modelId, &info)
           && not (isNull info) && info.MaxOutputTokens.HasValue
           && info.MaxOutputTokens.Value > 0L && info.MaxOutputTokens.Value < 4096L then
            let copy = ChatRequest(request.Model, request.Messages)
            copy.MaxTokens <- Nullable(int info.MaxOutputTokens.Value)
            copy.Temperature <- request.Temperature
            copy.TopP <- request.TopP
            copy.Stream <- request.Stream
            copy.Tools <- request.Tools
            copy.StopSequences <- request.StopSequences
            copy.ReasoningEffort <- request.ReasoningEffort
            copy.ReasoningBudgetTokens <- request.ReasoningBudgetTokens
            copy
        else request
    interface IChatModel with
        member _.Model = inner.Model
        member _.Complete(request, ct) = inner.Complete(prepare request, ct)
        member _.Stream(request, ct) = inner.Stream(prepare request, ct)

/// OAuth retries restart a request only before any stream event reaches the caller.
type private OAuthRetryModel(inner: IChatModel, invalidate: Action) =
    let isUnauthorized (error: exn) =
        match error with
        | :? AiException as ai -> ai.StatusCode.HasValue && ai.StatusCode.Value = 401
        | _ -> false

    interface IChatModel with
        member _.Model = inner.Model
        member _.Complete(request, ct) =
            task {
                try
                    return! inner.Complete(request, ct)
                with error when isUnauthorized error ->
                    invalidate.Invoke()
                    return! inner.Complete(request, ct)
            }

        member _.Stream(request, ct) =
            let produce (writer: System.Threading.Channels.ChannelWriter<ChatStreamEvent>) (token: CancellationToken) =
                task {
                    let mutable retry = true
                    let mutable attempted = false
                    let mutable emitted = false
                    while retry do
                        retry <- false
                        try
                            use iterator = inner.Stream(request, token).GetAsyncEnumerator(token)
                            let mutable more = true
                            while more do
                                let! moved = iterator.MoveNextAsync().AsTask()
                                more <- moved
                                if moved then
                                    emitted <- true
                                    do! writer.WriteAsync(iterator.Current, token).AsTask()
                        with error when not emitted && not attempted && isUnauthorized error ->
                            attempted <- true
                            invalidate.Invoke()
                            retry <- true
                }
            AsyncEnumerableBridge.ofProducerWithCancellation ct produce

/// Resolves a subscription's current inference endpoint only when sending a request.
type private AuthenticatedBackend(inner: IChatBackend, atBaseUrl: string -> string -> IChatBackend, sessions: IProviderAuthSessionProvider,
                                  providerId: string, accountId: string, modelId: string, options: ProviderOptions, explicitEndpoint: bool) =
    interface IChatBackend with
        member _.PrepareRequest(request, stream, ct) = task {
            let! session = sessions.GetSessionAsync(accountId, ct)
            if session.ProviderId <> providerId || session.AccountId <> accountId then
                raise (AiException("Subscription credentials do not match the requested provider and account."))
            if not (isNull options.DiscoveredModel) && options.DiscoveredModel.Authority <> session.Authority then
                raise (AiException("Discovered model belongs to another authentication authority."))
            if not (isNull options.DiscoveredModel) && options.DiscoveredModel.ModelId <> request.Model then
                raise (AiException("Discover the selected model before using different model-specific subscription settings."))
            let endpoint = if String.IsNullOrWhiteSpace session.ApiBaseUrl then options.BaseUrl else session.ApiBaseUrl
            if explicitEndpoint && not (String.IsNullOrWhiteSpace options.BaseUrl) then
                let configured, authenticated = Uri(options.BaseUrl), Uri(endpoint)
                if configured.GetLeftPart(UriPartial.Authority) <> authenticated.GetLeftPart(UriPartial.Authority) then
                    raise (AiException("Subscription credentials cannot be sent to a different API origin."))
            let! prepared = (atBaseUrl endpoint modelId).PrepareRequest(request, stream, ct)
            let headers = Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            for KeyValue(k,v) in prepared.Headers do headers[k] <- v
            if providerId = "github-copilot" then
                headers["User-Agent"] <- "GitHubCopilotChat/0.35.0"
                headers["Editor-Version"] <- "vscode/1.107.0"
                headers["Editor-Plugin-Version"] <- "copilot-chat/0.35.0"
                headers["Copilot-Integration-Id"] <- "vscode-chat"
                headers["Openai-Intent"] <- "conversation-edits"
                let last = if isNull request.Messages || request.Messages.Count = 0 then Role.User else request.Messages[request.Messages.Count - 1].Role
                headers["X-Initiator"] <- if last = Role.Tool || last = Role.Assistant then "agent" else "user"
                if not (isNull request.Messages) && request.Messages |> Seq.exists (fun m -> not (isNull m.Parts) && m.Parts |> Seq.exists (fun p -> p :? ImagePart)) then
                    headers["Copilot-Vision-Request"] <- "true"
            elif providerId = "kilo" then
                headers["X-KILOCODE-EDITORNAME"] <- "FSharp.ModelsDev"
                if not (String.IsNullOrWhiteSpace session.OrganizationId) then headers["X-KILOCODE-ORGANIZATIONID"] <- session.OrganizationId
            return PreparedChatRequest(prepared.Url, headers, prepared.Body)
        }
        member _.ParseResponse(body) = inner.ParseResponse(body)
        member _.CreateStreamParser() = inner.CreateStreamParser()
    interface IRequestScopedBackend with
        member _.ResolveBackend(requestModel) =
            let scoped = atBaseUrl options.BaseUrl requestModel
            AuthenticatedBackend(scoped, atBaseUrl, sessions, providerId, accountId, requestModel, options, explicitEndpoint) :> IChatBackend
    interface IStreamingCompletionBackend with
        member _.UseStreamingForCompletions =
            match inner with
            | :? IStreamingCompletionBackend as streaming -> streaming.UseStreamingForCompletions
            | _ -> false

/// Creates clients without accessing credentials or the network until a request is sent.
[<Sealed>]
type ProviderFactory(catalog: IModelCatalog, credentials: ICredentialProvider, registry: ProviderRegistry, transport: IChatTransport) =
    do
        if isNull (box catalog) then nullArg "catalog"
        if isNull (box credentials) then nullArg "credentials"

    let registry = if isNull registry then ProviderRegistry.Default else registry
    let transport = if isNull transport then HttpChatTransport() :> IChatTransport else transport

    new(catalog: IModelCatalog, credentials: ICredentialProvider) = ProviderFactory(catalog, credentials, null, null)

    /// Explicit options override catalog routing and may supply account-specific credentials.
    member _.CreateClient(providerId: string, accountId: string, modelId: string, options: ProviderOptions): IChatModel =
        if String.IsNullOrWhiteSpace providerId then invalidArg "providerId" "A provider id is required."
        if String.IsNullOrWhiteSpace modelId then invalidArg "modelId" "A model id is required."
        let mutable definition = Unchecked.defaultof<ProviderDefinition>
        if not (catalog.TryGetProvider(providerId, &definition)) then
            if providerId.Equals("antigravity", StringComparison.OrdinalIgnoreCase) then
                definition <- ProviderDefinition("antigravity", "Antigravity (unofficial compatibility adapter)", null, "antigravity", [||], null)
            elif providerId = "grok" then
                definition <- ProviderDefinition("grok", "Grok subscription", "https://cli-chat-proxy.grok.com/v1", "openai-chat", [||], null)
            elif providerId = "openai-codex" then
                definition <- ProviderDefinition("openai-codex", "ChatGPT Codex subscription", "https://chatgpt.com/backend-api/codex", "codex-responses", [||], null)
            else
                raise (AiException(AiError.Transport $"unknown provider: '{providerId}'"))

        let settings = ProviderOptions()
        if not (isNull options) then
            settings.ProtocolId <- options.ProtocolId
            settings.BaseUrl <- options.BaseUrl
            settings.ProjectId <- options.ProjectId
            settings.Location <- options.Location
            settings.Credentials <- options.Credentials
            if not (isNull options.DiscoveredModel) then
                let source = options.DiscoveredModel
                let copy = ProviderModelDescriptor(source.ProviderId, source.ModelId, source.ProtocolId, source.BaseUrl)
                copy.AccountId <- source.AccountId
                copy.Authority <- source.Authority
                copy.ModelInfo <- source.ModelInfo
                copy.SupportedEndpoints <- if isNull source.SupportedEndpoints then [||] else source.SupportedEndpoints |> Seq.toArray
                copy.ReasoningEfforts <- if isNull source.ReasoningEfforts then [||] else source.ReasoningEfforts |> Seq.toArray
                copy.SupportsThinkingToggle <- source.SupportsThinkingToggle
                copy.AlwaysThinking <- source.AlwaysThinking
                settings.DiscoveredModel <- copy
            settings.SessionId <- options.SessionId
            if not (isNull options.Headers) then
                let headers = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                for KeyValue(key, value) in options.Headers do headers.[key] <- value
                settings.Headers <- headers
        if not (isNull settings.DiscoveredModel) then
            let descriptor = settings.DiscoveredModel
            if descriptor.ProviderId <> providerId || descriptor.ModelId <> modelId
               || (not (String.IsNullOrWhiteSpace descriptor.AccountId) && descriptor.AccountId <> accountId) then
                raise (AiException("Discovered model does not match the requested provider, model and account."))
            if String.IsNullOrWhiteSpace settings.ProtocolId then settings.ProtocolId <- descriptor.ProtocolId
            if String.IsNullOrWhiteSpace settings.BaseUrl then settings.BaseUrl <- descriptor.BaseUrl
        let protocolId =
            if String.IsNullOrWhiteSpace settings.ProtocolId then registry.ResolveProtocol(providerId, modelId, catalog)
            else settings.ProtocolId
        settings.ProtocolId <- protocolId
        if String.IsNullOrWhiteSpace settings.BaseUrl then settings.BaseUrl <- definition.BaseUrl
        if String.IsNullOrWhiteSpace settings.BaseUrl && providerId = "xai" then settings.BaseUrl <- "https://api.x.ai/v1"
        if (protocolId = "openai-chat" || protocolId = "openai-compatible" || protocolId = "openai-responses" || protocolId = "codex-responses")
           && not (providerId.Equals("openai", StringComparison.OrdinalIgnoreCase))
           && String.IsNullOrWhiteSpace settings.BaseUrl then
            raise (AiException($"Provider '{providerId}' has no catalog API URL; set ProviderOptions.BaseUrl for this OpenAI-compatible endpoint."))
        let actualCredentials = if isNull (box settings.Credentials) then credentials else settings.Credentials
        let explicitEndpoint = not (isNull options) && not (String.IsNullOrWhiteSpace options.BaseUrl)
        let createBackend (requestModel: string) (backendSettings: ProviderOptions): IChatBackend =
            match protocolId with
            | "kimi-code" | "anthropic-messages" when providerId = "kimi-code-plan-cn" || providerId = "kimi-code-plan-global" ->
                KimiCodeBackend(requestModel, accountId, actualCredentials, backendSettings)
            | "openai-chat" | "openai-compatible" when providerId = "kimi-code-plan-cn" || providerId = "kimi-code-plan-global" ->
                KimiCodeBackend(requestModel, accountId, actualCredentials, backendSettings)
            | "openai-chat" | "openai-compatible" -> OpenAIChatBackend(requestModel, accountId, actualCredentials, backendSettings)
            | "openai-responses" | "codex-responses" -> OpenAIResponsesBackend(requestModel, accountId, actualCredentials, backendSettings)
            | "gemini" -> GeminiBackend(requestModel, accountId, actualCredentials, backendSettings)
            | "vertex-gemini" -> VertexGeminiBackend(requestModel, accountId, actualCredentials, backendSettings)
            | "anthropic-messages" -> AnthropicBackend(requestModel, accountId, actualCredentials, backendSettings)
            | "vertex-anthropic" -> VertexAnthropicBackend(requestModel, accountId, actualCredentials, backendSettings)
            | "antigravity" -> new AntigravityBackend(requestModel, accountId, actualCredentials, backendSettings)
            | _ -> raise (AiException(AiError.Transport $"protocol not supported: '{protocolId}'"))
        let backend = createBackend modelId settings
        let backend =
            match actualCredentials with
            | :? IProviderAuthSessionProvider as sessions ->
                let atBaseUrl url requestModel =
                    let copy = ProviderOptions()
                    copy.ProtocolId <- settings.ProtocolId
                    copy.BaseUrl <- url
                    copy.ProjectId <- settings.ProjectId
                    copy.Location <- settings.Location
                    copy.Credentials <- actualCredentials
                    copy.DiscoveredModel <- settings.DiscoveredModel
                    copy.SessionId <- settings.SessionId
                    createBackend requestModel copy
                AuthenticatedBackend(backend, atBaseUrl, sessions, providerId, accountId, modelId, settings, explicitEndpoint) :> IChatBackend
            | _ -> backend
        let backend = if isNull settings.Headers then backend else ConfiguredBackend(backend, settings.Headers) :> IChatBackend
        let client = ChatClient(modelId, backend, transport) :> IChatModel
        let client =
            if protocolId = "anthropic-messages" || protocolId = "vertex-anthropic" then
                CatalogTokenLimitModel(client, catalog, providerId) :> IChatModel
            else client
        match actualCredentials with
        | :? IRefreshableCredentialProvider as credentials when credentials.CanRefresh ->
            OAuthRetryModel(client, Action(fun () -> credentials.Invalidate(accountId))) :> IChatModel
        | :? IGoogleOAuthSessionProvider as sessions -> OAuthRetryModel(client, Action(fun () -> sessions.Invalidate(accountId))) :> IChatModel
        | _ -> client

    member this.CreateClient(providerId: string, accountId: string, modelId: string): IChatModel =
        this.CreateClient(providerId, accountId, modelId, null)

    /// Uses an explicitly discovered account model without modifying the offline catalog.
    member this.CreateClient(accountId: string, model: ProviderModelDescriptor): IChatModel =
        if isNull model then nullArg "model"
        let options = ProviderOptions()
        options.DiscoveredModel <- model
        this.CreateClient(model.ProviderId, accountId, model.ModelId, options)

    interface IChatClientFactory with
        member this.CreateClient(providerId, accountId, modelId) = this.CreateClient(providerId, accountId, modelId)
