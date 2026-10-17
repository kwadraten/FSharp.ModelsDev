module FSharp.ModelsDev.Catalog.Generator.CatalogParser

open System
open System.Collections.Generic
open System.Text.Json
open FSharp.ModelsDev.Catalog

let protocolIdFor (npm: string) =
    match npm with
    | "antigravity" -> "antigravity"
    | "@ai-sdk/openai" | "@ai-sdk/openai-compatible" -> "openai-chat"
    | "@ai-sdk/anthropic" -> "anthropic-messages"
    | "@ai-sdk/google" -> "gemini"
    | "@ai-sdk/google-vertex" -> "vertex-gemini"
    | "@ai-sdk/google-vertex/anthropic" -> "vertex-anthropic"
    | _ -> "unknown"

let tryString (name: string) (element: JsonElement) : string =
    match element.TryGetProperty(name) with
    | true, p when p.ValueKind = JsonValueKind.String -> p.GetString()
    | _ -> null

let readStringArray (element: JsonElement) : string[] =
    if element.ValueKind = JsonValueKind.Array then
        [| for item in element.EnumerateArray() do
             if item.ValueKind = JsonValueKind.String then
                 yield item.GetString() |]
    else
        Array.empty

let readProvider (providerId: string) (root: JsonElement) : ProviderDefinition * JsonElement =
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

let hasModality (direction: string) (value: string) (modalities: JsonElement) =
    if modalities.ValueKind <> JsonValueKind.Object then false
    else
        match modalities.TryGetProperty(direction) with
        | true, items when items.ValueKind = JsonValueKind.Array ->
            let mutable found = false
            for item in items.EnumerateArray() do
                if not found && item.ValueKind = JsonValueKind.String && item.GetString() = value then
                    found <- true
            found
        | _ -> false

let readCapabilities (model: JsonElement) =
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

let defaultReasoningEfforts : IReadOnlyList<string> = [| "low"; "medium"; "high" |]

let readReasoningEfforts (model: JsonElement) : IReadOnlyList<string> =
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

let readModel (providerId: string) (modelId: string) (model: JsonElement) : ModelInfo =
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
    let readBool (name: string) =
        match model.TryGetProperty(name) with
        | true, p when p.ValueKind = JsonValueKind.True -> Nullable true
        | true, p when p.ValueKind = JsonValueKind.False -> Nullable false
        | _ -> Nullable()
    let readModalities (direction: string) =
        match model.TryGetProperty("modalities") with
        | true, modalities when modalities.ValueKind = JsonValueKind.Object ->
            match modalities.TryGetProperty(direction) with
            | true, values -> readStringArray values :> IReadOnlyList<string>
            | _ -> Array.empty<string> :> IReadOnlyList<string>
        | _ -> Array.empty<string> :> IReadOnlyList<string>
    let metadata = ModelMetadata()
    metadata.MaxInputTokens <- (match model.TryGetProperty("limit") with
                                | true, limit when limit.ValueKind = JsonValueKind.Object -> readLimit "input" limit
                                | _ -> Nullable())
    metadata.InputModalities <- readModalities "input"
    metadata.OutputModalities <- readModalities "output"
    metadata.Description <- tryString "description" model
    metadata.Family <- tryString "family" model
    metadata.ModelType <- tryString "type" model
    metadata.Status <- tryString "status" model
    metadata.KnowledgeCutoff <- tryString "knowledge" model
    metadata.ReleaseDate <- tryString "release_date" model
    metadata.LastUpdated <- tryString "last_updated" model
    metadata.SupportsAttachments <- readBool "attachment"
    metadata.SupportsTemperature <- readBool "temperature"
    metadata.OpenWeights <- readBool "open_weights"
    ModelInfo(
        providerId,
        modelId,
        displayName,
        canonicalModelId,
        readCapabilities model,
        contextLimit,
        maxOutputTokens,
        readReasoningEfforts model,
        metadata)


let parseModel(providerId: string, modelId: string, json: string) =
    use document = JsonDocument.Parse(json)
    readModel providerId modelId document.RootElement
