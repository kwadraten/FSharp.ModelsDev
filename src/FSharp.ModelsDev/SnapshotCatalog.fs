namespace FSharp.ModelsDev.Catalog

open System
open System.Collections.Concurrent
open System.Collections.Frozen
open System.Collections.Generic
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text.Json

/// IModelCatalog over the embedded models.dev api.json snapshot.
/// Provider definitions are parsed once up front; per-model data is parsed lazily per provider.
[<Sealed>]
type SnapshotCatalog() =

    static let resourceName = "FSharp.ModelsDev.models.snapshot.json"

    static let openResource () =
        let assembly = typeof<SnapshotCatalog>.Assembly
        match assembly.GetManifestResourceStream(resourceName) with
        | null -> invalidOp $"Embedded resource '{resourceName}' was not found in {assembly.FullName}."
        | stream -> stream

    static let protocolIdFor (npm: string) =
        match npm with
        | "@ai-sdk/openai" | "@ai-sdk/openai-compatible" -> "openai-chat"
        | "@ai-sdk/anthropic" -> "anthropic-messages"
        | "@ai-sdk/google" -> "gemini"
        | _ -> "unknown"

    static let tryString (name: string) (element: JsonElement) : string =
        match element.TryGetProperty(name) with
        | true, p when p.ValueKind = JsonValueKind.String -> p.GetString()
        | _ -> null

    static let readStringArray (element: JsonElement) : string[] =
        if element.ValueKind = JsonValueKind.Array then
            [| for item in element.EnumerateArray() do
                 if item.ValueKind = JsonValueKind.String then
                     yield item.GetString() |]
        else
            Array.empty

    static let readProvider (providerId: string) (root: JsonElement) : ProviderDefinition * JsonElement =
        let name =
            match tryString "name" root with
            | null | "" -> providerId
            | s -> s
        let baseUrl = tryString "api" root
        let protocolId =
            match tryString "npm" root with
            | null -> "unknown"
            | npm -> protocolIdFor npm
        let envVars =
            match root.TryGetProperty("env") with
            | true, e -> readStringArray e :> IReadOnlyList<string>
            | _ -> Array.empty<string> :> IReadOnlyList<string>
        let docUrl = tryString "doc" root
        let modelsElement =
            match root.TryGetProperty("models") with
            | true, m when m.ValueKind = JsonValueKind.Object -> m
            | _ -> JsonElement()
        (ProviderDefinition(providerId, name, baseUrl, protocolId, envVars, docUrl), modelsElement)

    static let hasModality (direction: string) (value: string) (modalities: JsonElement) =
        match modalities.TryGetProperty(direction) with
        | true, items when items.ValueKind = JsonValueKind.Array ->
            let mutable found = false
            for item in items.EnumerateArray() do
                if not found && item.ValueKind = JsonValueKind.String && item.GetString() = value then
                    found <- true
            found
        | _ -> false

    static let readCapabilities (model: JsonElement) =
        let mutable caps = ModelCapabilities.None
        let flag (name: string) (capability: ModelCapabilities) =
            match model.TryGetProperty(name) with
            | true, p when p.ValueKind = JsonValueKind.True -> caps <- caps ||| capability
            | _ -> ()
        flag "tool_call" ModelCapabilities.ToolCalling
        flag "structured_output" ModelCapabilities.StructuredOutput
        flag "reasoning" ModelCapabilities.Reasoning
        match model.TryGetProperty("modalities") with
        | true, modalities ->
            if hasModality "input" "text" modalities then caps <- caps ||| ModelCapabilities.TextInput
            if hasModality "input" "image" modalities then caps <- caps ||| ModelCapabilities.ImageInput
            if hasModality "input" "audio" modalities then caps <- caps ||| ModelCapabilities.AudioInput
        | _ -> ()
        caps

    static let defaultReasoningEfforts : IReadOnlyList<string> = [| "low"; "medium"; "high" |]

    static let readReasoningEfforts (model: JsonElement) : IReadOnlyList<string> =
        let isReasoning =
            match model.TryGetProperty("reasoning") with
            | true, p -> p.ValueKind = JsonValueKind.True
            | _ -> false
        if not isReasoning then
            Array.empty<string> :> IReadOnlyList<string>
        else
            let efforts = ResizeArray<string>()
            match model.TryGetProperty("reasoning_options") with
            | true, options when options.ValueKind = JsonValueKind.Array ->
                for opt in options.EnumerateArray() do
                    if tryString "type" opt = "effort" then
                        match opt.TryGetProperty("values") with
                        | true, values when values.ValueKind = JsonValueKind.Array ->
                            for v in values.EnumerateArray() do
                                if v.ValueKind = JsonValueKind.String then
                                    efforts.Add(v.GetString())
                        | _ -> ()
            | _ -> ()
            if efforts.Count > 0 then efforts :> IReadOnlyList<string> else defaultReasoningEfforts

    static let readModel (providerId: string) (modelId: string) (model: JsonElement) : ModelInfo =
        let displayName =
            match tryString "name" model with
            | null | "" -> modelId
            | s -> s
        let canonicalModelId =
            match tryString "canonical_model_id" model with
            | null | "" -> providerId + "/" + modelId
            | s -> s
        let readLimit (name: string) (limit: JsonElement) : Nullable<int64> =
            match limit.TryGetProperty(name) with
            | true, p when p.ValueKind = JsonValueKind.Number ->
                match p.TryGetInt64() with
                | true, v -> Nullable v
                | false, _ -> Nullable()
            | _ -> Nullable()
        let contextLimit, maxOutputTokens =
            match model.TryGetProperty("limit") with
            | true, limit when limit.ValueKind = JsonValueKind.Object ->
                readLimit "context" limit, readLimit "output" limit
            | _ -> Nullable(), Nullable()
        ModelInfo(
            providerId,
            modelId,
            displayName,
            canonicalModelId,
            readCapabilities model,
            contextLimit,
            maxOutputTokens,
            readReasoningEfforts model)

    static let emptyModels: IReadOnlyList<ModelInfo> = Array.empty<ModelInfo> :> IReadOnlyList<ModelInfo>

    static let document = lazy (
        use stream = openResource()
        JsonDocument.Parse(stream, JsonDocumentOptions(AllowTrailingCommas = true))
    )

    static let providers = lazy (
        let dictionary = Dictionary<string, ProviderDefinition * JsonElement>(StringComparer.Ordinal)
        for property in document.Value.RootElement.EnumerateObject() do
            if property.Value.ValueKind = JsonValueKind.Object then
                dictionary.[property.Name] <- readProvider property.Name property.Value
        FrozenDictionary.ToFrozenDictionary(dictionary, StringComparer.Ordinal)
    )

    static let providerIds = lazy (
        let ids = Array.ofSeq providers.Value.Keys
        Array.sortInPlace ids
        ids :> IReadOnlyList<string>
    )

    static let snapshotVersion = lazy (
        use stream = openResource()
        let hash = SHA256.HashData(stream)
        Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 12)
    )

    static let modelCache =
        ConcurrentDictionary<string, IReadOnlyList<ModelInfo> * FrozenDictionary<string, ModelInfo>>(StringComparer.Ordinal)

    static let getProviderModels (providerId: string) (modelsElement: JsonElement) =
        modelCache.GetOrAdd(
            providerId,
            fun _ ->
                let models = ResizeArray<ModelInfo>()
                let index = Dictionary<string, ModelInfo>(StringComparer.Ordinal)
                if modelsElement.ValueKind = JsonValueKind.Object then
                    for property in modelsElement.EnumerateObject() do
                        if property.Value.ValueKind = JsonValueKind.Object then
                            let info = readModel providerId property.Name property.Value
                            models.Add(info)
                            index.[info.ModelId] <- info
                (models :> IReadOnlyList<ModelInfo>, FrozenDictionary.ToFrozenDictionary(index, StringComparer.Ordinal))
        )

    static let findProvider (providerId: string) : ProviderDefinition =
        if isNull providerId then
            Unchecked.defaultof<ProviderDefinition>
        else
            match providers.Value.TryGetValue(providerId) with
            | true, (definition, _) -> definition
            | _ -> Unchecked.defaultof<ProviderDefinition>

    static let findModel (providerId: string) (modelId: string) : ModelInfo =
        if isNull providerId || isNull modelId then
            Unchecked.defaultof<ModelInfo>
        else
            match providers.Value.TryGetValue(providerId) with
            | true, (_, modelsElement) ->
                let _, index = getProviderModels providerId modelsElement
                match index.TryGetValue(modelId) with
                | true, info -> info
                | _ -> Unchecked.defaultof<ModelInfo>
            | _ -> Unchecked.defaultof<ModelInfo>

    static let getModels (providerId: string) : IReadOnlyList<ModelInfo> =
        if isNull providerId then
            emptyModels
        else
            match providers.Value.TryGetValue(providerId) with
            | true, (_, modelsElement) -> fst (getProviderModels providerId modelsElement)
            | _ -> emptyModels

    static let defaultInstance = SnapshotCatalog()

    interface IModelCatalog with
        member _.SnapshotVersion = snapshotVersion.Value
        member _.ProviderIds = providerIds.Value
        member _.GetModels(providerId: string) : IReadOnlyList<ModelInfo> = getModels providerId

        member _.TryGetProvider(providerId: string, [<Out>] provider: byref<ProviderDefinition>) : bool =
            let definition = findProvider providerId
            provider <- definition
            not (isNull definition)

        member _.TryGet(providerId: string, modelId: string, [<Out>] model: byref<ModelInfo>) : bool =
            let info = findModel providerId modelId
            model <- info
            not (isNull info)

    /// SHA256 (first 12 lowercase hex chars) of the embedded snapshot resource.
    member this.SnapshotVersion: string = (this :> IModelCatalog).SnapshotVersion

    /// Provider ids present in the snapshot, sorted ordinally.
    member this.ProviderIds: IReadOnlyList<string> = (this :> IModelCatalog).ProviderIds

    /// Looks up a provider definition; returns false when the provider id is unknown.
    member this.TryGetProvider(providerId: string, [<Out>] provider: byref<ProviderDefinition>) : bool =
        let mutable result = Unchecked.defaultof<ProviderDefinition>
        let ok = (this :> IModelCatalog).TryGetProvider(providerId, &result)
        provider <- result
        ok

    /// Looks up a single model; returns false when the provider or model id is unknown.
    member this.TryGet(providerId: string, modelId: string, [<Out>] model: byref<ModelInfo>) : bool =
        let mutable result = Unchecked.defaultof<ModelInfo>
        let ok = (this :> IModelCatalog).TryGet(providerId, modelId, &result)
        model <- result
        ok

    /// All models offered by a provider; empty when the provider id is unknown.
    member this.GetModels(providerId: string) : IReadOnlyList<ModelInfo> =
        (this :> IModelCatalog).GetModels(providerId)

    /// Shared default instance.
    static member Default: SnapshotCatalog = defaultInstance
