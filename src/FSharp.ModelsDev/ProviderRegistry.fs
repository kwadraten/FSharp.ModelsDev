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
            registry.Register("openai-codex", "codex-responses")
            registry.Register("openrouter", "openai-chat")
            registry.Register("deepinfra", "openai-chat")
            registry.Register("groq", "openai-chat")
            registry.Register("together", "openai-chat")
            registry.Register("togetherai", "openai-chat")
            registry.Register("fireworks", "openai-chat")
            registry.Register("fireworks-ai", "openai-chat")
            registry.Register("mistral", "openai-chat")
            registry.Register("xai", "openai-chat")
            registry.Register("grok", "openai-chat")
            registry.Register("kimi-code-plan-cn", "kimi-code")
            registry.Register("kimi-code-plan-global", "kimi-code")
            registry.Register("github-copilot", "openai-chat")
            registry.Register("kilo", "openai-chat")
            registry.Register("cline-pass", "openai-chat")
            registry.Register("anthropic", "anthropic-messages")
            registry.Register("google", "gemini")
            registry.Register("google-vertex", "vertex-gemini")
            registry.Register("google-vertex-anthropic", "vertex-anthropic")
            registry.Register("antigravity", "antigravity")
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

    /// Model overrides come first. Specialized service adapters take precedence over generic catalog transports.
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

            let specialized =
                match providers.TryGetValue(providerId) with
                | true, "kimi-code" -> "kimi-code"
                | _ -> null
            match specialized, catalogProtocolId with
            | protocolId, _ when not (isNull protocolId) -> protocolId
            | _, null ->
                match providers.TryGetValue(providerId) with
                | (true, protocolId) -> protocolId
                | _ -> "unknown"
            | _, protocolId -> protocolId

    /// Shared registry with curated protocol defaults for well-known providers.
    static member Default: ProviderRegistry = defaultRegistry.Force()
