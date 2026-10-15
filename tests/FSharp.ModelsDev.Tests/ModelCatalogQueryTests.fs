namespace FSharp.ModelsDev.Tests

open System
open System.Collections.Generic
open Xunit
open FSharp.ModelsDev.Catalog

type private FakeModelCatalog(models: ModelInfo array) =
    interface IModelCatalog with
        member _.SnapshotVersion = "test"
        member _.ProviderIds =
            let providers =
                models
                |> Seq.map (fun model -> model.ProviderId)
                |> Seq.distinct
                |> Seq.toArray
            Array.AsReadOnly(providers) :> IReadOnlyList<string>
        member _.TryGetProvider(_, provider: byref<ProviderDefinition>) =
            provider <- null
            false
        member _.TryGet(_, _, model: byref<ModelInfo>) =
            model <- null
            false
        member _.GetModels(providerId) =
            let matching = models |> Array.filter (fun model -> model.ProviderId = providerId)
            Array.AsReadOnly(matching) :> IReadOnlyList<ModelInfo>

module private ModelCatalogQueryTestData =
    let model provider modelId family status context input output inputs outputs capabilities attachments temperature weights =
        let metadata = ModelMetadata()
        metadata.Family <- family
        metadata.Status <- status
        metadata.MaxInputTokens <- Nullable input
        metadata.InputModalities <- inputs
        metadata.OutputModalities <- outputs
        metadata.SupportsAttachments <- Nullable attachments
        metadata.SupportsTemperature <- Nullable temperature
        metadata.OpenWeights <- Nullable weights
        ModelInfo(provider, modelId, modelId, modelId, capabilities, Nullable context, Nullable output,
                  Array.empty<string>, metadata)

    let sampleModels () =
        [| model "z-provider" "z-model" "family-a" "deprecated" 100L 60L 20L
               [| "text"; "image" |] [| "text" |]
               (ModelCapabilities.TextInput ||| ModelCapabilities.ImageInput ||| ModelCapabilities.ToolCalling)
               true false true
           model "a-provider" "b-model" "family-b" "active" 200L 120L 40L
               [| "text" |] [| "text"; "image" |]
               (ModelCapabilities.TextInput ||| ModelCapabilities.ToolCalling ||| ModelCapabilities.Reasoning)
               false true false
           model "a-provider" "a-model" "family-a" "active" 50L 30L 10L
               [| "text" |] [| "text" |] ModelCapabilities.TextInput
               true true false
           ModelInfo("a-provider", "unknown", "unknown", "unknown", ModelCapabilities.None,
               Nullable<int64>(), Nullable<int64>(), Array.empty<string>) |]

type ModelCatalogQueryTests() =

    [<Fact>]
    member _.``combined filters use AND across fields and alternatives within identifiers`` () =
        let filter = ModelFilter()
        filter.ProviderIds <- [| "z-provider"; "a-provider" |]
        filter.Families <- [| "family-a" |]
        filter.Statuses <- [| "active"; "deprecated" |]
        filter.MinContextTokens <- Nullable 50L
        filter.MinInputTokens <- Nullable 30L
        filter.MinOutputTokens <- Nullable 10L
        filter.RequiredInputModalities <- [| "text" |]
        filter.RequiredCapabilities <- ModelCapabilities.TextInput
        filter.SupportsAttachments <- Nullable true

        let found = ModelCatalogQuery.FilterModels(ModelCatalogQueryTestData.sampleModels(), filter)

        Assert.Equal<string>([| "a-model"; "z-model" |], found |> Seq.map (fun model -> model.ModelId) |> Seq.toArray)

    [<Fact>]
    member _.``missing numeric and boolean metadata does not satisfy requested conditions`` () =
        let filter = ModelFilter()
        filter.MinInputTokens <- Nullable 0L
        filter.OpenWeights <- Nullable false
        filter.SupportsTemperature <- Nullable true
        let found = ModelCatalogQuery.FilterModels(ModelCatalogQueryTestData.sampleModels(), filter)
        Assert.Equal<string>([| "a-model"; "b-model" |], found |> Seq.map (fun model -> model.ModelId) |> Seq.toArray)

        filter.SupportsAttachments <- Nullable false
        let noAttachments = ModelCatalogQuery.FilterModels(ModelCatalogQueryTestData.sampleModels(), filter)
        Assert.Equal<string>([| "b-model" |], noAttachments |> Seq.map (fun model -> model.ModelId) |> Seq.toArray)

        let falseFilter = ModelFilter(SupportsTemperature = Nullable false)
        let falseValues = ModelCatalogQuery.FilterModels(ModelCatalogQueryTestData.sampleModels(), falseFilter)
        Assert.Equal<string>([| "z-model" |], falseValues |> Seq.map (fun model -> model.ModelId) |> Seq.toArray)

    [<Fact>]
    member _.``zero thresholds are valid while negative thresholds throw`` () =
        let filter = ModelFilter()
        filter.MinContextTokens <- Nullable 0L
        Assert.Equal(3, ModelCatalogQuery.FilterModels(ModelCatalogQueryTestData.sampleModels(), filter).Count)
        filter.MinContextTokens <- Nullable -1L
        Assert.Throws<ArgumentOutOfRangeException>(fun () ->
            ModelCatalogQuery.FilterModels(ModelCatalogQueryTestData.sampleModels(), filter) |> ignore)
        |> ignore

        filter.MinContextTokens <- Nullable<int64>()
        filter.MinInputTokens <- Nullable -1L
        Assert.Throws<ArgumentOutOfRangeException>(fun () ->
            ModelCatalogQuery.FilterModels(ModelCatalogQueryTestData.sampleModels(), filter) |> ignore)
        |> ignore

        filter.MinInputTokens <- Nullable<int64>()
        filter.MinOutputTokens <- Nullable -1L
        let catalog = FakeModelCatalog(ModelCatalogQueryTestData.sampleModels()) :> IModelCatalog
        Assert.Throws<ArgumentOutOfRangeException>(fun () -> ModelCatalogQuery.FindModels(catalog, filter) |> ignore)
        |> ignore

    [<Fact>]
    member _.``required modalities use ordinal matching and require every value`` () =
        let filter = ModelFilter()
        filter.RequiredInputModalities <- [| "text"; "image" |]
        Assert.Equal<string>([| "z-model" |],
            ModelCatalogQuery.FilterModels(ModelCatalogQueryTestData.sampleModels(), filter)
            |> Seq.map (fun model -> model.ModelId) |> Seq.toArray)
        filter.RequiredInputModalities <- [| "Text" |]
        Assert.Empty(ModelCatalogQuery.FilterModels(ModelCatalogQueryTestData.sampleModels(), filter))

        filter.RequiredInputModalities <- Array.empty
        filter.RequiredOutputModalities <- [| "text"; "image" |]
        Assert.Equal<string>([| "b-model" |],
            ModelCatalogQuery.FilterModels(ModelCatalogQueryTestData.sampleModels(), filter)
            |> Seq.map (fun model -> model.ModelId) |> Seq.toArray)

    [<Fact>]
    member _.``model types and all required capability flags are combined`` () =
        let metadata = ModelMetadata()
        metadata.ModelType <- "chat"
        let complete = ModelInfo("p", "z", "z", "z",
            ModelCapabilities.TextInput ||| ModelCapabilities.ToolCalling ||| ModelCapabilities.Reasoning,
            Nullable 1L, Nullable 1L, Array.empty<string>, metadata)
        let partial = ModelInfo("p", "a", "a", "a",
            ModelCapabilities.TextInput ||| ModelCapabilities.ToolCalling,
            Nullable 1L, Nullable 1L, Array.empty<string>, metadata)
        let filter = ModelFilter()
        filter.ModelTypes <- [| "chat" |]
        filter.RequiredCapabilities <- ModelCapabilities.TextInput ||| ModelCapabilities.ToolCalling ||| ModelCapabilities.Reasoning
        let found = ModelCatalogQuery.FilterModels([| complete; partial |], filter)
        Assert.Equal<string>([| "z" |], found |> Seq.map (fun model -> model.ModelId) |> Seq.toArray)

    [<Fact>]
    member _.``find models crosses providers and sorts ordinally including non-active statuses`` () =
        let catalog = FakeModelCatalog(ModelCatalogQueryTestData.sampleModels()) :> IModelCatalog
        let found = ModelCatalogQuery.FindModels(catalog, null)
        Assert.Equal<string>([| "a-provider/a-model"; "a-provider/b-model"; "a-provider/unknown"; "z-provider/z-model" |],
            found |> Seq.map (fun model -> model.ProviderId + "/" + model.ModelId) |> Seq.toArray)

    [<Fact>]
    member _.``filter models also sorts ordinally`` () =
        let found = ModelCatalogQuery.FilterModels(ModelCatalogQueryTestData.sampleModels(), null)
        Assert.Equal<string>([| "a-provider/a-model"; "a-provider/b-model"; "a-provider/unknown"; "z-provider/z-model" |],
            found |> Seq.map (fun model -> model.ProviderId + "/" + model.ModelId) |> Seq.toArray)

    [<Fact>]
    member _.``null and empty filters have no restrictions and results are read-only`` () =
        let models = ModelCatalogQueryTestData.sampleModels()
        let nullFilter = ModelCatalogQuery.FilterModels(models, null)
        let emptyFilter = ModelCatalogQuery.FilterModels(models, ModelFilter())
        Assert.Equal(models.Length, nullFilter.Count)
        Assert.Equal(models.Length, emptyFilter.Count)
        Assert.Throws<NotSupportedException>(fun () -> (nullFilter :?> IList<ModelInfo>).Clear()) |> ignore

    [<Fact>]
    member _.``filter values are snapshotted before eager enumeration`` () =
        let filter = ModelFilter()
        let providers = ResizeArray<string>([ "p" ])
        filter.ProviderIds <- providers :> IReadOnlyList<string>
        let matching = ModelInfo("p", "model", "model", "model", ModelCapabilities.None,
                                 Nullable<int64>(), Nullable<int64>(), Array.empty<string>)
        let source = seq {
            providers.Clear()
            providers.Add("elsewhere")
            yield matching
        }

        let results = ModelCatalogQuery.FilterModels(source, filter)
        Assert.Single(results) |> ignore
        Assert.Equal("p", results.[0].ProviderId)

    [<Fact>]
    member _.``null catalog and model sequence throw`` () =
        Assert.Throws<ArgumentNullException>(fun () -> ModelCatalogQuery.FindModels(null, null) |> ignore) |> ignore
        Assert.Throws<ArgumentNullException>(fun () -> ModelCatalogQuery.FilterModels(null, null) |> ignore) |> ignore
