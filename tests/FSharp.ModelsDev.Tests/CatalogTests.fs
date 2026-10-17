namespace FSharp.ModelsDev.Tests

open System
open Xunit
open FSharp.ModelsDev.Catalog

type CatalogTests() =

    [<Fact>]
    member _.``ProviderIds contains openai`` () =
        let catalog = SnapshotCatalog()
        Assert.Contains("openai", catalog.ProviderIds)

    [<Fact>]
    member _.``snapshot version is populated`` () =
        let catalog = SnapshotCatalog()
        Assert.False(String.IsNullOrWhiteSpace(catalog.SnapshotVersion))

    [<Fact>]
    member _.``TryGet resolves gpt-5 with capabilities`` () =
        let catalog = SnapshotCatalog()
        let mutable model = Unchecked.defaultof<ModelInfo>

        let found = catalog.TryGet("openai", "gpt-5", &model)

        Assert.True(found)
        Assert.NotNull(model)
        Assert.Equal("gpt-5", model.ModelId)
        Assert.Equal("openai", model.ProviderId)
        Assert.False(String.IsNullOrWhiteSpace(model.DisplayName))
        Assert.False(String.IsNullOrWhiteSpace(model.CanonicalModelId))
        Assert.True(model.SupportsTools)
        Assert.True(model.SupportsReasoning)
        Assert.True(model.ContextLimit.HasValue)
        Assert.True(model.ContextLimit.Value > 0L)

    [<Fact>]
    member _.``TryGet unknown model returns false`` () =
        let catalog = SnapshotCatalog()
        let mutable model = Unchecked.defaultof<ModelInfo>
        Assert.False(catalog.TryGet("openai", "model-that-does-not-exist-12345", &model))

    [<Fact>]
    member _.``TryGet unknown provider returns false`` () =
        let catalog = SnapshotCatalog()
        let mutable model = Unchecked.defaultof<ModelInfo>
        Assert.False(catalog.TryGet("provider-that-does-not-exist", "gpt-5", &model))

    [<Fact>]
    member _.``GetModels returns openai models`` () =
        let catalog = SnapshotCatalog()
        let models = catalog.GetModels("openai")

        Assert.NotEmpty(models)
        Assert.Contains(models, fun m -> m.ModelId = "gpt-5")

    [<Fact>]
    member _.``GetModels unknown provider is empty`` () =
        let catalog = SnapshotCatalog()
        Assert.Empty(catalog.GetModels("provider-that-does-not-exist"))

    [<Fact>]
    member _.``openai provider uses openai-chat protocol`` () =
        let catalog = SnapshotCatalog()
        let mutable provider = Unchecked.defaultof<ProviderDefinition>

        let found = catalog.TryGetProvider("openai", &provider)

        Assert.True(found)
        Assert.NotNull(provider)
        Assert.Equal("openai", provider.ProviderId)
        Assert.False(String.IsNullOrWhiteSpace(provider.Name))
        Assert.Equal("openai-chat", provider.ProtocolId)

    [<Fact>]
    member _.``TryGetProvider unknown returns false`` () =
        let catalog = SnapshotCatalog()
        let mutable provider = Unchecked.defaultof<ProviderDefinition>
        Assert.False(catalog.TryGetProvider("provider-that-does-not-exist", &provider))

    [<Fact>]
    member _.``Antigravity is an offline provider with subscription scoped models`` () =
        let catalog = SnapshotCatalog()
        let mutable provider = Unchecked.defaultof<ProviderDefinition>
        Assert.Contains("antigravity", catalog.ProviderIds)
        Assert.True(catalog.TryGetProvider("antigravity", &provider))
        Assert.Equal("antigravity", provider.ProtocolId)
        Assert.Empty(catalog.GetModels("antigravity"))
        let mutable model = Unchecked.defaultof<ModelInfo>
        Assert.False(catalog.TryGet("antigravity", "gemini-2.5-pro", &model))
