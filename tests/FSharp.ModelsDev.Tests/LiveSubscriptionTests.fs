namespace FSharp.ModelsDev.Tests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog
open FSharp.ModelsDev.Providers

module private LiveSubscription =
    // Host-supplied tokens only: live tests never read another application's credentials or launch a login.
    let run (options: ProviderAuthOptions) (prefix: string) = task {
        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 90.)
        let ct = timeout.Token
        let value suffix = Environment.GetEnvironmentVariable("MODELSDEV_" + prefix + "_" + suffix)
        let modelId = value "MODEL"
        let store = InMemoryProviderAuthSessionStore() :> IProviderAuthSessionStore
        let session = ProviderAuthSession(options.ProviderId, options.Authority, "live-test")
        session.ApiBaseUrl <- options.ApiBaseUrl
        session.AccessToken <- value "TOKEN"
        session.RefreshToken <- value "REFRESH_TOKEN"
        if options.ProviderId = "github-copilot" then
            session.CredentialKind <- ProviderCredentialKind.Copilot
            session.UpstreamToken <- value "TOKEN"
            session.AccessToken <- null
            session.ExpiresAt <- Nullable(DateTimeOffset.UtcNow)
        elif options.ProviderId = "openrouter" then session.CredentialKind <- ProviderCredentialKind.ApiKey
        do! store.SaveAsync(session, ct)
        use auth = new ProviderAuthClient(options, store)
        let! discovered = auth.DiscoverModelsAsync("live-test", ct)
        let selected = discovered |> Seq.tryFind (fun model -> model.ModelId = modelId)
        Assert.True(selected.IsSome, "Configured model is absent from the authenticated discovery result.")
        let factory = ProviderFactory(SnapshotCatalog(), auth.CreateCredentialProvider())
        let client = factory.CreateClient("live-test", selected.Value)
        let request = ChatRequest(modelId, [| ChatMessage.User "Reply with the single word OK." |])
        request.MaxTokens <- Nullable 128
        let! response = client.Complete(request, ct)
        Assert.False(String.IsNullOrWhiteSpace response.Message.Text)
        use iterator = client.Stream(request, ct).GetAsyncEnumerator(ct)
        let mutable more = true
        let mutable terminal = 0
        let text = Text.StringBuilder()
        while more do
            let! moved = iterator.MoveNextAsync().AsTask()
            more <- moved
            if moved then
                match iterator.Current with
                | :? TextDelta as delta -> text.Append(delta.Text) |> ignore
                | :? Completed -> terminal <- terminal + 1
                | _ -> ()
        Assert.False(String.IsNullOrWhiteSpace(text.ToString()))
        Assert.Equal(1, terminal)
    }

type LiveSubscriptionTests() =
    [<LiveBackendFact([| "MODELSDEV_GROK_TOKEN"; "MODELSDEV_GROK_MODEL" |]); Trait("Category", "Live")>]
    member _.``live Grok subscription``(): Task = LiveSubscription.run (ProviderAuthOptions.Grok()) "GROK"

    [<LiveBackendFact([| "MODELSDEV_OPENROUTER_TOKEN"; "MODELSDEV_OPENROUTER_MODEL" |]); Trait("Category", "Live")>]
    member _.``live OpenRouter account``(): Task = LiveSubscription.run (ProviderAuthOptions.OpenRouter()) "OPENROUTER"

    [<LiveBackendFact([| "MODELSDEV_KIMI_TOKEN"; "MODELSDEV_KIMI_MODEL" |]); Trait("Category", "Live")>]
    member _.``live Kimi Code subscription``(): Task = LiveSubscription.run (ProviderAuthOptions.KimiCode(false)) "KIMI"

    [<LiveBackendFact([| "MODELSDEV_COPILOT_TOKEN"; "MODELSDEV_COPILOT_MODEL" |]); Trait("Category", "Live")>]
    member _.``live Copilot subscription``(): Task = LiveSubscription.run (ProviderAuthOptions.GitHubCopilot()) "COPILOT"

    [<LiveBackendFact([| "MODELSDEV_KILO_TOKEN"; "MODELSDEV_KILO_MODEL" |]); Trait("Category", "Live")>]
    member _.``live Kilo account``(): Task = LiveSubscription.run (ProviderAuthOptions.Kilo()) "KILO"

    [<LiveBackendFact([| "MODELSDEV_CLINEPASS_TOKEN"; "MODELSDEV_CLINEPASS_MODEL" |]); Trait("Category", "Live")>]
    member _.``live ClinePass subscription``(): Task = LiveSubscription.run (ProviderAuthOptions.ClinePass()) "CLINEPASS"
