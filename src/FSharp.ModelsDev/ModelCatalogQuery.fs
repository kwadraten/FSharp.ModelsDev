namespace FSharp.ModelsDev.Catalog

open System
open System.Collections.Generic

/// Eager, offline filtering over catalog model offerings.
[<AbstractClass; Sealed>]
type ModelCatalogQuery private () =

    static member private SnapshotStrings(values: IReadOnlyList<string>) =
        if isNull values then Array.empty<string>
        else values |> Seq.toArray

    static member private ValidateThreshold(name: string, value: Nullable<int64>) =
        if value.HasValue && value.Value < 0L then
            raise (ArgumentOutOfRangeException(name, value.Value, "Token thresholds must be non-negative."))

    static member private FilterSnapshot(filter: ModelFilter) =
        if isNull filter then
            (Array.empty, Array.empty, Array.empty, Array.empty,
             Nullable<int64>(), Nullable<int64>(), Nullable<int64>(),
             Array.empty, Array.empty, ModelCapabilities.None,
             Nullable<bool>(), Nullable<bool>(), Nullable<bool>())
        else
            ModelCatalogQuery.ValidateThreshold("MinContextTokens", filter.MinContextTokens)
            ModelCatalogQuery.ValidateThreshold("MinInputTokens", filter.MinInputTokens)
            ModelCatalogQuery.ValidateThreshold("MinOutputTokens", filter.MinOutputTokens)
            (ModelCatalogQuery.SnapshotStrings(filter.ProviderIds),
             ModelCatalogQuery.SnapshotStrings(filter.Families),
             ModelCatalogQuery.SnapshotStrings(filter.ModelTypes),
             ModelCatalogQuery.SnapshotStrings(filter.Statuses),
             filter.MinContextTokens, filter.MinInputTokens, filter.MinOutputTokens,
             ModelCatalogQuery.SnapshotStrings(filter.RequiredInputModalities),
             ModelCatalogQuery.SnapshotStrings(filter.RequiredOutputModalities),
             filter.RequiredCapabilities,
             filter.SupportsAttachments, filter.SupportsTemperature, filter.OpenWeights)

    static member private Matches(model: ModelInfo, snapshot) =
        let providers, families, modelTypes, statuses, minContext, minInput, minOutput,
            inputModalities, outputModalities, requiredCapabilities,
            attachments, temperature, openWeights = snapshot

        let inValues (values: string array) (actual: string) =
            values.Length = 0 || values |> Array.exists (fun expected -> String.Equals(expected, actual, StringComparison.Ordinal))

        let hasAll (required: string array) (actual: IReadOnlyList<string>) =
            required.Length = 0 ||
            (not (isNull actual) && required |> Array.forall (fun wanted -> actual |> Seq.exists (fun value -> String.Equals(value, wanted, StringComparison.Ordinal))))

        let meetsMinimum (minimum: Nullable<int64>) (actual: Nullable<int64>) =
            not minimum.HasValue || (actual.HasValue && actual.Value >= minimum.Value)

        let matchesBool (requested: Nullable<bool>) (actual: Nullable<bool>) =
            not requested.HasValue || (actual.HasValue && actual.Value = requested.Value)

        not (isNull model) &&
        inValues providers model.ProviderId &&
        inValues families model.Family &&
        inValues modelTypes model.ModelType &&
        inValues statuses model.Status &&
        meetsMinimum minContext model.ContextLimit &&
        meetsMinimum minInput model.MaxInputTokens &&
        meetsMinimum minOutput model.MaxOutputTokens &&
        hasAll inputModalities model.InputModalities &&
        hasAll outputModalities model.OutputModalities &&
        (requiredCapabilities = ModelCapabilities.None ||
         (model.Capabilities &&& requiredCapabilities) = requiredCapabilities) &&
        matchesBool attachments model.SupportsAttachments &&
        matchesBool temperature model.SupportsTemperature &&
        matchesBool openWeights model.OpenWeights

    /// Filters models across every provider in an offline catalog.
    static member FindModels(catalog: IModelCatalog, filter: ModelFilter) : IReadOnlyList<ModelInfo> =
        if isNull catalog then nullArg "catalog"
        let snapshot = ModelCatalogQuery.FilterSnapshot(filter)
        let models =
            seq {
                for providerId in catalog.ProviderIds do
                    for model in catalog.GetModels(providerId) do
                        if ModelCatalogQuery.Matches(model, snapshot) then yield model
            }
            |> Seq.sortWith (fun left right ->
                let providerOrder = StringComparer.Ordinal.Compare(left.ProviderId, right.ProviderId)
                if providerOrder <> 0 then providerOrder
                else StringComparer.Ordinal.Compare(left.ModelId, right.ModelId))
            |> Seq.toArray
        Array.AsReadOnly(models) :> IReadOnlyList<ModelInfo>

    /// Filters an arbitrary model sequence using the same rules as FindModels.
    static member FilterModels(models: IEnumerable<ModelInfo>, filter: ModelFilter) : IReadOnlyList<ModelInfo> =
        if isNull models then nullArg "models"
        let snapshot = ModelCatalogQuery.FilterSnapshot(filter)
        let result =
            models
            |> Seq.filter (fun model -> ModelCatalogQuery.Matches(model, snapshot))
            |> Seq.sortWith (fun left right ->
                let providerOrder = StringComparer.Ordinal.Compare(left.ProviderId, right.ProviderId)
                if providerOrder <> 0 then providerOrder
                else StringComparer.Ordinal.Compare(left.ModelId, right.ModelId))
            |> Seq.toArray
        Array.AsReadOnly(result) :> IReadOnlyList<ModelInfo>
