open System
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog
open FSharp.ModelsDev.MEAi

let catalog = SnapshotCatalog()
let found, model = catalog.TryGet("openai", "gpt-5")
if not found || not model.MaxInputTokens.HasValue then
    failwith "The packaged offline catalog is unavailable."

let filter =
    ModelFilter(
        RequiredInputModalities = [| "image" |],
        RequiredCapabilities = ModelCapabilities.ToolCalling,
        MinContextTokens = Nullable 131072L,
        MinOutputTokens = Nullable 8192L)
let matches = ModelCatalogQuery.FindModels(catalog, filter)
if matches.Count = 0 then failwith "Catalog filtering returned no models."

let client = ChatClient.OpenAI("package-smoke-placeholder", "gpt-5")
let checkAdapter () =
    use adapter = client.AsIChatClient()
    printfn "F# package consumer: %s, %d matching models." catalog.SnapshotVersion matches.Count
checkAdapter ()
