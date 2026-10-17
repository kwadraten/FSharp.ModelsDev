namespace FSharp.ModelsDev.Tests

open System
open System.Collections.Generic
open System.Text.Json
open Xunit
open FSharp.ModelsDev.Catalog

type CatalogMetadataTests() =

    [<Fact>]
    member _.``metadata parses snapshot fields and preserves unknown modalities`` () =
        let json = """{"description":"desc","family":"fam","type":"chat","status":"active","knowledge":"2025-01","release_date":"2025-02-01","last_updated":"2025-03-01","attachment":false,"temperature":true,"open_weights":false,"modalities":{"input":["text","point-cloud",17,null],"output":["text","audio",false]},"limit":{"input":0}}"""
        let model = FSharp.ModelsDev.Catalog.Generator.CatalogParser.parseModel("p", "m", json)
        Assert.Equal("desc", model.Description)
        Assert.Equal("fam", model.Family)
        Assert.Equal("chat", model.ModelType)
        Assert.Equal("active", model.Status)
        Assert.Equal("2025-01", model.KnowledgeCutoff)
        Assert.Equal("2025-02-01", model.ReleaseDate)
        Assert.Equal("2025-03-01", model.LastUpdated)
        Assert.False(model.SupportsAttachments.Value)
        Assert.True(model.SupportsTemperature.Value)
        Assert.False(model.OpenWeights.Value)
        Assert.Equal(0L, model.MaxInputTokens.Value)
        Assert.Equal<string>([| "text"; "point-cloud" |], Seq.toArray model.InputModalities)
        Assert.Equal<string>([| "text"; "audio" |], Seq.toArray model.OutputModalities)
        Assert.True(model.Capabilities.HasFlag ModelCapabilities.TextInput)
        Assert.False(model.Capabilities.HasFlag ModelCapabilities.ImageInput)

    [<Theory>]
    [<InlineData("{}")>]
    [<InlineData("{\"description\":null,\"family\":4,\"type\":false,\"status\":[],\"knowledge\":{},\"release_date\":null,\"last_updated\":1,\"attachment\":null,\"temperature\":0,\"open_weights\":\"false\",\"limit\":null,\"modalities\":null}")>]
    [<InlineData("{\"limit\":[],\"modalities\":{\"input\":null,\"output\":{}}}")>]
    member _.``missing null and malformed metadata remains unknown without throwing`` (json: string) =
        let model = FSharp.ModelsDev.Catalog.Generator.CatalogParser.parseModel("p", "m", json)
        Assert.Null(model.Description)
        Assert.False(model.MaxInputTokens.HasValue)
        Assert.False(model.SupportsAttachments.HasValue)
        Assert.False(model.SupportsTemperature.HasValue)
        Assert.False(model.OpenWeights.HasValue)
        Assert.Empty(model.InputModalities)
        Assert.Empty(model.OutputModalities)

    [<Fact>]
    member _.``metadata parsing keeps provider scoped model identity`` () =
        let a = FSharp.ModelsDev.Catalog.Generator.CatalogParser.parseModel("provider-a", "model", """{"canonical_model_id":"shared/model","limit":{"context":101,"input":91,"output":11},"modalities":{"input":["image"],"output":["text"]}}""")
        let b = FSharp.ModelsDev.Catalog.Generator.CatalogParser.parseModel("provider-b", "model", """{"canonical_model_id":"shared/model","limit":{"context":202,"input":182,"output":22},"modalities":{"input":["audio"],"output":["json"]}}""")
        Assert.Equal("provider-a", a.ProviderId)
        Assert.Equal("provider-b", b.ProviderId)
        Assert.Equal("shared/model", a.CanonicalModelId)
        Assert.Equal("shared/model", b.CanonicalModelId)
        Assert.Equal(101L, a.ContextLimit.Value)
        Assert.Equal(91L, a.MaxInputTokens.Value)
        Assert.Equal(11L, a.MaxOutputTokens.Value)
        Assert.Equal(202L, b.ContextLimit.Value)
        Assert.Equal(182L, b.MaxInputTokens.Value)
        Assert.Equal(22L, b.MaxOutputTokens.Value)
        Assert.Equal("image", a.InputModalities.[0])
        Assert.Equal("audio", b.InputModalities.[0])
        Assert.Equal("text", a.OutputModalities.[0])
        Assert.Equal("json", b.OutputModalities.[0])

    [<Fact>]
    member _.``zero limits are retained and absent input is not inferred`` () =
        let zero = FSharp.ModelsDev.Catalog.Generator.CatalogParser.parseModel("p", "zero", """{"limit":{"context":0,"input":0,"output":0}}""")
        let missing = FSharp.ModelsDev.Catalog.Generator.CatalogParser.parseModel("p", "missing", """{"limit":{"context":12,"output":4}}""")
        Assert.Equal(0L, zero.ContextLimit.Value)
        Assert.Equal(0L, zero.MaxInputTokens.Value)
        Assert.Equal(0L, zero.MaxOutputTokens.Value)
        Assert.False(missing.MaxInputTokens.HasValue)

    [<Theory>]
    [<InlineData("1.5")>]
    [<InlineData("9223372036854775808")>]
    member _.``malformed numeric input limits remain unknown`` (value: string) =
        let model = FSharp.ModelsDev.Catalog.Generator.CatalogParser.parseModel("p", "m", "{\"limit\":{\"input\":" + value + "}}")
        Assert.False(model.MaxInputTokens.HasValue)

    [<Fact>]
    member _.``legacy constructor keeps default metadata`` () =
        let model = ModelInfo("p", "m", "m", "p/m", ModelCapabilities.None, Nullable(), Nullable(), Array.empty<string>)
        Assert.Null(model.Description)
        Assert.False(model.MaxInputTokens.HasValue)
        Assert.Empty(model.InputModalities)

    [<Fact>]
    member _.``model snapshots metadata and exposes read only modality copies`` () =
        let values = ResizeArray<string>([ "text" ])
        let outputs = ResizeArray<string>([ "json" ])
        let reasoning = ResizeArray<string>([ "low" ])
        let metadata = ModelMetadata()
        metadata.InputModalities <- values
        metadata.OutputModalities <- outputs
        metadata.Description <- "before"
        let model = ModelInfo("p", "m", "m", "p/m", ModelCapabilities.None, Nullable(), Nullable(), reasoning, metadata)
        values.[0] <- "image"
        outputs.[0] <- "xml"
        reasoning.[0] <- "max"
        metadata.Description <- "after"
        metadata.InputModalities <- Array.empty<string>
        metadata.OutputModalities <- Array.empty<string>
        Assert.Equal("before", model.Description)
        Assert.Equal("text", model.InputModalities.[0])
        Assert.Equal("json", model.OutputModalities.[0])
        Assert.Equal("low", model.ReasoningEfforts.[0])
        let collection = model.InputModalities :?> IList<string>
        Assert.Throws<NotSupportedException>(fun () -> collection.[0] <- "audio") |> ignore
        let outputCollection = model.OutputModalities :?> IList<string>
        Assert.Throws<NotSupportedException>(fun () -> outputCollection.[0] <- "yaml") |> ignore

    [<Fact>]
    member _.``embedded gpt 5 token limits are preserved`` () =
        let catalog = SnapshotCatalog()
        let mutable model = Unchecked.defaultof<ModelInfo>
        Assert.True(catalog.TryGet("openai", "gpt-5", &model))
        Assert.Equal(400000L, model.ContextLimit.Value)
        Assert.Equal(272000L, model.MaxInputTokens.Value)
        Assert.Equal(128000L, model.MaxOutputTokens.Value)
        Assert.Equal<string>([| "text"; "image" |], Seq.toArray model.InputModalities)
        Assert.Equal<string>([| "text" |], Seq.toArray model.OutputModalities)

    [<Fact>]
    member _.``compiled models preserve every source field without an embedded JSON resource`` () =
        let catalog = SnapshotCatalog()
        Assert.DoesNotContain("FSharp.ModelsDev.models.snapshot.json", typeof<SnapshotCatalog>.Assembly.GetManifestResourceNames())
        use doc = JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "models.snapshot.json")))
        for provider in doc.RootElement.EnumerateObject() do
            let expectedProvider, models = FSharp.ModelsDev.Catalog.Generator.CatalogParser.readProvider provider.Name provider.Value
            let mutable actualProvider = Unchecked.defaultof<ProviderDefinition>
            Assert.True(catalog.TryGetProvider(provider.Name, &actualProvider))
            Assert.Equal(expectedProvider.Name, actualProvider.Name)
            Assert.Equal(expectedProvider.BaseUrl, actualProvider.BaseUrl)
            Assert.Equal(expectedProvider.ProtocolId, actualProvider.ProtocolId)
            Assert.Equal(expectedProvider.DocUrl, actualProvider.DocUrl)
            Assert.Equal<string>(expectedProvider.EnvVars, actualProvider.EnvVars)
            Assert.Equal(models.EnumerateObject() |> Seq.length, catalog.GetModels(provider.Name).Count)
            for source in models.EnumerateObject() do
                let expected = FSharp.ModelsDev.Catalog.Generator.CatalogParser.readModel provider.Name source.Name source.Value
                let mutable actual = Unchecked.defaultof<ModelInfo>
                Assert.True(catalog.TryGet(provider.Name, source.Name, &actual))
                // Compare all public fields, including nullable metadata and ordered modality/effort arrays.
                Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual))
