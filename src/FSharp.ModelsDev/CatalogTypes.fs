namespace FSharp.ModelsDev.Catalog

open System
open System.Collections.Generic

[<Flags>]
type ModelCapabilities =
    | None = 0
    | TextInput = 1
    | ImageInput = 2
    | AudioInput = 4
    | ToolCalling = 8
    | StructuredOutput = 16
    | Reasoning = 32

/// Internal capability description; a model may carry several of these at once.
type internal ReasoningControl =
    | Toggle
    | Effort of levels: string list
    | BudgetTokens of minimum: int option * maximum: int option

/// One model as offered by one provider (the catalog's ModelOffering).
[<Sealed; AllowNullLiteral>]
type ModelInfo
    (
        providerId: string,
        modelId: string,
        displayName: string,
        canonicalModelId: string,
        capabilities: ModelCapabilities,
        contextLimit: Nullable<int64>,
        maxOutputTokens: Nullable<int64>,
        reasoningEfforts: IReadOnlyList<string>
    ) =
    member _.ProviderId = providerId
    member _.ModelId = modelId
    member _.DisplayName = displayName
    member _.CanonicalModelId = canonicalModelId
    member _.Capabilities = capabilities
    member _.ContextLimit = contextLimit
    member _.MaxOutputTokens = maxOutputTokens
    member _.ReasoningEfforts = reasoningEfforts

    member _.SupportsTools = capabilities.HasFlag ModelCapabilities.ToolCalling
    member _.SupportsReasoning = capabilities.HasFlag ModelCapabilities.Reasoning
    member _.SupportsStructuredOutput = capabilities.HasFlag ModelCapabilities.StructuredOutput

[<Sealed; AllowNullLiteral>]
type ProviderDefinition
    (
        providerId: string,
        name: string,
        baseUrl: string,
        protocolId: string,
        envVars: IReadOnlyList<string>,
        docUrl: string
    ) =
    member _.ProviderId = providerId
    member _.Name = name
    member _.BaseUrl = baseUrl
    member _.ProtocolId = protocolId
    member _.EnvVars = envVars
    member _.DocUrl = docUrl
