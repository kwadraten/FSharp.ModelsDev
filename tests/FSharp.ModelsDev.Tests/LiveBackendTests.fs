namespace FSharp.ModelsDev.Tests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog
open FSharp.ModelsDev.Providers

/// Live requests are opt-in and never initiate an interactive OAuth login.
type LiveBackendFactAttribute(required: string[]) as this =
    inherit FactAttribute()
    do
        if Environment.GetEnvironmentVariable("MODELSDEV_LIVE_TESTS") <> "1" then
            this.Skip <- "Set MODELSDEV_LIVE_TESTS=1 to enable live provider requests."
        elif required |> Array.exists (fun name -> String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable name)) then
            this.Skip <- "Missing live configuration: " + String.Join(", ", required)

module private LiveBackend =
    let run provider protocol keyVariable modelVariable = task {
        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90.))
        let key = Environment.GetEnvironmentVariable keyVariable
        let model = Environment.GetEnvironmentVariable modelVariable
        let credentials =
            match provider with
            | "google" -> ApiKeyCredentialProvider.Gemini(key) :> ICredentialProvider
            | "anthropic" -> ApiKeyCredentialProvider.Anthropic(key) :> ICredentialProvider
            | _ -> ApiKeyCredentialProvider(key) :> ICredentialProvider
        let factory = ProviderFactory(SnapshotCatalog(), credentials)
        let options = ProviderOptions(ProtocolId = protocol)
        if provider.StartsWith("google-vertex", StringComparison.Ordinal) then
            options.ProjectId <- Environment.GetEnvironmentVariable("GOOGLE_VERTEX_PROJECT")
            options.Location <- Environment.GetEnvironmentVariable("GOOGLE_VERTEX_LOCATION")
        if provider = "antigravity" then options.ProjectId <- Environment.GetEnvironmentVariable("MODELSDEV_ANTIGRAVITY_PROJECT")
        let client = factory.CreateClient(provider, "live-test", model, options)
        let request = ChatRequest(model, [| ChatMessage.User "Reply with the single word OK." |])
        request.MaxTokens <- Nullable 128
        let! response = client.Complete(request, timeout.Token)
        Assert.False(String.IsNullOrWhiteSpace(response.Message.Text))
        let mutable text = ""
        let mutable completed = 0
        use iterator = client.Stream(request, timeout.Token).GetAsyncEnumerator(timeout.Token)
        let mutable more = true
        while more do
            let! moved = iterator.MoveNextAsync().AsTask()
            more <- moved
            if moved then
                match iterator.Current with
                | :? TextDelta as value -> text <- text + value.Text
                | :? Completed -> completed <- completed + 1
                | _ -> ()
        Assert.False(String.IsNullOrWhiteSpace text)
        Assert.Equal(1, completed)
    }

type LiveBackendTests() =
    [<LiveBackendFact([| "MODELSDEV_OPENAI_KEY"; "MODELSDEV_OPENAI_MODEL" |]); Trait("Category", "Live")>]
    member _.``live OpenAI Chat Completions`` () : Task = LiveBackend.run "openai" "openai-chat" "MODELSDEV_OPENAI_KEY" "MODELSDEV_OPENAI_MODEL"

    [<LiveBackendFact([| "MODELSDEV_OPENAI_KEY"; "MODELSDEV_OPENAI_MODEL" |]); Trait("Category", "Live")>]
    member _.``live OpenAI Responses`` () : Task = LiveBackend.run "openai" "openai-responses" "MODELSDEV_OPENAI_KEY" "MODELSDEV_OPENAI_MODEL"

    [<LiveBackendFact([| "MODELSDEV_GEMINI_KEY"; "MODELSDEV_GEMINI_MODEL" |]); Trait("Category", "Live")>]
    member _.``live Gemini`` () : Task = LiveBackend.run "google" "gemini" "MODELSDEV_GEMINI_KEY" "MODELSDEV_GEMINI_MODEL"

    [<LiveBackendFact([| "MODELSDEV_VERTEX_TOKEN"; "MODELSDEV_VERTEX_MODEL"; "GOOGLE_VERTEX_PROJECT"; "GOOGLE_VERTEX_LOCATION" |]); Trait("Category", "Live")>]
    member _.``live Vertex Gemini`` () : Task = LiveBackend.run "google-vertex" "vertex-gemini" "MODELSDEV_VERTEX_TOKEN" "MODELSDEV_VERTEX_MODEL"

    [<LiveBackendFact([| "MODELSDEV_ANTHROPIC_KEY"; "MODELSDEV_ANTHROPIC_MODEL" |]); Trait("Category", "Live")>]
    member _.``live Anthropic`` () : Task = LiveBackend.run "anthropic" "anthropic-messages" "MODELSDEV_ANTHROPIC_KEY" "MODELSDEV_ANTHROPIC_MODEL"

    [<LiveBackendFact([| "MODELSDEV_VERTEX_TOKEN"; "MODELSDEV_VERTEX_CLAUDE_MODEL"; "GOOGLE_VERTEX_PROJECT"; "GOOGLE_VERTEX_LOCATION" |]); Trait("Category", "Live")>]
    member _.``live Vertex Claude`` () : Task = LiveBackend.run "google-vertex-anthropic" "vertex-anthropic" "MODELSDEV_VERTEX_TOKEN" "MODELSDEV_VERTEX_CLAUDE_MODEL"

    [<LiveBackendFact([| "MODELSDEV_ANTIGRAVITY_TOKEN"; "MODELSDEV_ANTIGRAVITY_MODEL"; "MODELSDEV_ANTIGRAVITY_PROJECT" |]); Trait("Category", "Live")>]
    member _.``live Antigravity`` () : Task = LiveBackend.run "antigravity" "antigravity" "MODELSDEV_ANTIGRAVITY_TOKEN" "MODELSDEV_ANTIGRAVITY_MODEL"
