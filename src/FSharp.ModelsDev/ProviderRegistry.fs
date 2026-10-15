namespace FSharp.ModelsDev.Providers

open System
open System.Collections.Generic
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog

/// Maps provider/model ids to protocol ids, with per-model overrides on top of catalog data.
[<Sealed; AllowNullLiteral>]
type ProviderRegistry() =

    let providers = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    let modelOverrides = Dictionary<string * string, string>()

    static let defaultRegistry =
        lazy (
            let registry = ProviderRegistry()
            registry.Register("openai", "openai-chat")
            registry.Register("openrouter", "openai-chat")
            registry.Register("deepinfra", "openai-chat")
            registry.Register("groq", "openai-chat")
            registry.Register("together", "openai-chat")
            registry.Register("togetherai", "openai-chat")
            registry.Register("fireworks", "openai-chat")
            registry.Register("fireworks-ai", "openai-chat")
            registry.Register("mistral", "openai-chat")
            registry.Register("xai", "openai-chat")
            registry.Register("anthropic", "anthropic-messages")
            registry.Register("google", "gemini")
            registry
        )

    /// Registers (or overrides) the protocol id for a provider.
    member _.Register(providerId: string, protocolId: string) =
        if isNull providerId then
            nullArg "providerId"

        if isNull protocolId then
            nullArg "protocolId"

        providers.[providerId] <- protocolId

    /// Registers a protocol id for one specific model of a provider.
    member _.RegisterModelOverride(providerId: string, modelId: string, protocolId: string) =
        if isNull providerId then
            nullArg "providerId"

        if isNull modelId then
            nullArg "modelId"

        if isNull protocolId then
            nullArg "protocolId"

        modelOverrides.[(providerId, modelId)] <- protocolId

    /// Resolves the protocol id: model override, then the catalog's provider definition, then registry registrations, then "unknown".
    member _.ResolveProtocol(providerId: string, modelId: string, catalog: IModelCatalog): string =
        match modelOverrides.TryGetValue((providerId, modelId)) with
        | (true, protocolId) -> protocolId
        | _ ->
            let mutable definition = Unchecked.defaultof<ProviderDefinition>

            let catalogProtocolId =
                if not (isNull (box catalog))
                   && catalog.TryGetProvider(providerId, &definition)
                   && not (isNull (box definition))
                   && not (String.IsNullOrWhiteSpace definition.ProtocolId)
                   && definition.ProtocolId <> "unknown" then
                    definition.ProtocolId
                else
                    null

            match catalogProtocolId with
            | null ->
                match providers.TryGetValue(providerId) with
                | (true, protocolId) -> protocolId
                | _ -> "unknown"
            | protocolId -> protocolId

    /// Shared registry with curated protocol defaults for well-known providers.
    static member Default: ProviderRegistry = defaultRegistry.Force()
