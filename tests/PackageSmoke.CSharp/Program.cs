using System;
using System.Linq;
using FSharp.ModelsDev;
using FSharp.ModelsDev.Catalog;
using FSharp.ModelsDev.MEAi;

var catalog = new SnapshotCatalog();
if (!catalog.TryGet("openai", "gpt-5", out var model) || !model.MaxInputTokens.HasValue)
    throw new InvalidOperationException("The packaged offline catalog is unavailable.");

var matches = ModelCatalogQuery.FindModels(catalog, new ModelFilter
{
    RequiredInputModalities = new[] { "image" },
    RequiredCapabilities = ModelCapabilities.ToolCalling,
    MinContextTokens = 131072L,
    MinOutputTokens = 8192L
});
if (!matches.Any()) throw new InvalidOperationException("Catalog filtering returned no models.");

// Construction and MEAI adaptation must remain offline.
var client = ChatClient.OpenAI("package-smoke-placeholder", "gpt-5");
using var adapter = client.AsIChatClient();
Console.WriteLine($"C# package consumer: {catalog.SnapshotVersion}, {matches.Count} matching models.");
