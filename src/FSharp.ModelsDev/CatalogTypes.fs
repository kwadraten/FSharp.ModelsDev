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

/// Optional catalog metadata supplied when constructing a model. ModelInfo snapshots these values.
[<Sealed; AllowNullLiteral>]
type ModelMetadata() =
    member val MaxInputTokens = Nullable<int64>() with get, set
    member val InputModalities: IReadOnlyList<string> = Array.empty<string> with get, set
    member val OutputModalities: IReadOnlyList<string> = Array.empty<string> with get, set
    member val Description: string = null with get, set
    member val Family: string = null with get, set
    member val ModelType: string = null with get, set
    member val Status: string = null with get, set
    member val KnowledgeCutoff: string = null with get, set
    member val ReleaseDate: string = null with get, set
    member val LastUpdated: string = null with get, set
    member val SupportsAttachments = Nullable<bool>() with get, set
    member val SupportsTemperature = Nullable<bool>() with get, set
    member val OpenWeights = Nullable<bool>() with get, set

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
        reasoningEfforts: IReadOnlyList<string>,
        metadata: ModelMetadata
    ) =
    let copyStrings (values: IReadOnlyList<string>) : IReadOnlyList<string> =
        let items = if isNull values then Array.empty else Seq.toArray values
        Array.AsReadOnly(items) :> IReadOnlyList<string>
    let metadata = if isNull metadata then ModelMetadata() else metadata
    let maxInputTokens = metadata.MaxInputTokens
    let inputModalities = copyStrings metadata.InputModalities
    let outputModalities = copyStrings metadata.OutputModalities
    let description, family, modelType, status = metadata.Description, metadata.Family, metadata.ModelType, metadata.Status
    let knowledgeCutoff, releaseDate, lastUpdated = metadata.KnowledgeCutoff, metadata.ReleaseDate, metadata.LastUpdated
    let supportsAttachments, supportsTemperature, openWeights = metadata.SupportsAttachments, metadata.SupportsTemperature, metadata.OpenWeights
    let reasoningEfforts = copyStrings reasoningEfforts

    new(providerId, modelId, displayName, canonicalModelId, capabilities, contextLimit, maxOutputTokens, reasoningEfforts) =
        ModelInfo(providerId, modelId, displayName, canonicalModelId, capabilities, contextLimit, maxOutputTokens, reasoningEfforts, null)

    /// Provider-declared input token limit; unknown when absent. Not derived from context/output limits.
    member _.MaxInputTokens = maxInputTokens
    /// Input modality identifiers exactly as declared by the source, including unknown future identifiers.
    member _.InputModalities = inputModalities
    /// Output modality identifiers. Catalog support does not imply SDK transport support.
    member _.OutputModalities = outputModalities
    member _.Description = description
    member _.Family = family
    member _.ModelType = modelType
    member _.Status = status
    /// Source date string, preserving year/month precision; null when unknown.
    member _.KnowledgeCutoff = knowledgeCutoff
    member _.ReleaseDate = releaseDate
    member _.LastUpdated = lastUpdated
    /// Null means the source does not declare attachment support.
    member _.SupportsAttachments = supportsAttachments
    /// Whether temperature is supported; null when the source does not declare it.
    member _.SupportsTemperature = supportsTemperature
    /// Whether model weights are publicly available; null when unknown.
    member _.OpenWeights = openWeights
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

/// All populated conditions are required; values inside each identifier list are alternatives.
[<Sealed; AllowNullLiteral>]
type ModelFilter() =
    member val ProviderIds: IReadOnlyList<string> = Array.empty<string> with get, set
    member val Families: IReadOnlyList<string> = Array.empty<string> with get, set
    member val ModelTypes: IReadOnlyList<string> = Array.empty<string> with get, set
    member val Statuses: IReadOnlyList<string> = Array.empty<string> with get, set
    member val MinContextTokens = Nullable<int64>() with get, set
    member val MinInputTokens = Nullable<int64>() with get, set
    member val MinOutputTokens = Nullable<int64>() with get, set
    member val RequiredInputModalities: IReadOnlyList<string> = Array.empty<string> with get, set
    member val RequiredOutputModalities: IReadOnlyList<string> = Array.empty<string> with get, set
    member val RequiredCapabilities = ModelCapabilities.None with get, set
    member val SupportsAttachments = Nullable<bool>() with get, set
    member val SupportsTemperature = Nullable<bool>() with get, set
    member val OpenWeights = Nullable<bool>() with get, set

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
