namespace FSharp.ModelsDev.Providers

open System
open System.Runtime.InteropServices
open System.Threading
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog

/// Creates IChatModel instances from provider/model ids.
[<AllowNullLiteral>]
type IChatClientFactory =
    abstract CreateClient: providerId: string * accountId: string * modelId: string -> IChatModel

/// Default factory: catalog lookup, protocol resolution, credentials, and protocol wiring.
[<Sealed>]
type ProviderFactory
    (
        catalog: IModelCatalog,
        credentials: ICredentialProvider,
        registry: ProviderRegistry,
        transport: IChatTransport
    ) =

    do
        if isNull (box catalog) then
            nullArg "catalog"

        if isNull (box credentials) then
            nullArg "credentials"

    let registry = if isNull registry then ProviderRegistry.Default else registry

    new(catalog: IModelCatalog, credentials: ICredentialProvider) =
        ProviderFactory(catalog, credentials, null, null)

    /// Creates a chat model; throws AiException for unknown providers or unsupported protocols.
    member _.CreateClient(providerId: string, accountId: string, modelId: string): IChatModel =
        let mutable definition = Unchecked.defaultof<ProviderDefinition>

        if not (catalog.TryGetProvider(providerId, &definition)) then
            raise (AiException(AiError.Transport $"unknown provider: '{providerId}'"))

        let protocolId = registry.ResolveProtocol(providerId, modelId, catalog)

        match protocolId with
        | "openai-chat"
        | "openai-compatible" ->
            // Credentials are local (env vars or static keys), so a one-shot sync-over-async is acceptable in this synchronous factory method.
            let headers = credentials.GetHeaders(accountId, CancellationToken.None).GetAwaiter().GetResult()

            let actualTransport =
                if isNull transport then
                    HttpChatTransport() :> IChatTransport
                else
                    transport

            ChatClient(
                modelId,
                OpenAICompatibleChatProtocol(),
                actualTransport,
                definition.BaseUrl + "/chat/completions",
                headers
            )
            :> IChatModel
        | _ ->
            raise (AiException(AiError.Transport $"protocol not supported: '{protocolId}'"))

    interface IChatClientFactory with
        member this.CreateClient(providerId: string, accountId: string, modelId: string) =
            this.CreateClient(providerId, accountId, modelId)
