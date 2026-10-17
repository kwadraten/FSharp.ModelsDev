namespace FSharp.ModelsDev.Catalog

open System
open System.Collections.Concurrent
open System.Collections.Frozen
open System.Collections.Generic
open System.Runtime.InteropServices

/// Offline catalog compiled from the models.dev snapshot and SDK supplements.
/// Models are constructed lazily per provider without runtime JSON parsing.
[<Sealed>]
type SnapshotCatalog() =

    static let emptyModels: IReadOnlyList<ModelInfo> = Array.empty<ModelInfo>
    static let providers = lazy (
        let dictionary = Dictionary<string, ProviderDefinition * Lazy<IReadOnlyList<ModelInfo>>>(StringComparer.Ordinal)
        for definition, models in Generated.CatalogData.providers do
            dictionary.Add(definition.ProviderId, (definition, models))
        FrozenDictionary.ToFrozenDictionary(dictionary, StringComparer.Ordinal))
    static let providerIds = lazy (
        providers.Value.Keys |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(a, b)) |> Seq.toArray |> Array.AsReadOnly
        :> IReadOnlyList<string>)
    static let modelCache =
        ConcurrentDictionary<string, IReadOnlyList<ModelInfo> * FrozenDictionary<string, ModelInfo>>(StringComparer.Ordinal)
    static let getProviderModels providerId (models: Lazy<IReadOnlyList<ModelInfo>>) =
        modelCache.GetOrAdd(providerId, fun _ ->
            let values = models.Value
            let index = Dictionary<string, ModelInfo>(StringComparer.Ordinal)
            for model in values do index.Add(model.ModelId, model)
            values, FrozenDictionary.ToFrozenDictionary(index, StringComparer.Ordinal))
    static let findProvider providerId =
        if isNull providerId then null
        else
            match providers.Value.TryGetValue(providerId) with
            | true, (definition, _) -> definition
            | _ -> null
    static let findModel providerId modelId =
        if isNull providerId || isNull modelId then null
        else
            match providers.Value.TryGetValue(providerId) with
            | true, (_, models) ->
                let _, index = getProviderModels providerId models
                match index.TryGetValue(modelId) with
                | true, model -> model
                | _ -> null
            | _ -> null
    static let getModels providerId =
        if isNull providerId then emptyModels
        else
            match providers.Value.TryGetValue(providerId) with
            | true, (_, models) -> fst (getProviderModels providerId models)
            | _ -> emptyModels
    static let defaultInstance = SnapshotCatalog()

    interface IModelCatalog with
        member _.SnapshotVersion = Generated.CatalogData.snapshotVersion
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

    /// SHA256 (first 12 lowercase hex chars) of the source models.dev snapshot.
    member this.SnapshotVersion: string = (this :> IModelCatalog).SnapshotVersion

    /// Provider ids in the snapshot and SDK supplements, sorted ordinally.
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
