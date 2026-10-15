namespace FSharp.ModelsDev.Tests

open System
open System.Threading
open System.Threading.Tasks
open System.Net
open System.Net.Http
open System.Text
open System.Collections.Generic
open System.Net.Sockets
open Xunit
open FSharp.ModelsDev.Providers
open FSharp.ModelsDev

type ProviderAuthCoreTests() =
    [<Fact>]
    member _.``in memory provider auth store isolates authority and account and clones sessions``() = task {
        let store = InMemoryProviderAuthSessionStore() :> IProviderAuthSessionStore
        let original = ProviderAuthSession("grok", "issuer|client|domain|url", "work")
        original.AccessToken <- "access-one"
        original.RefreshToken <- "refresh-one"
        original.ExpiresAt <- Nullable(DateTimeOffset.UtcNow.AddHours 1.)
        original.DeviceId <- "device-one"
        do! store.SaveAsync(original, CancellationToken.None)

        original.AccessToken <- "mutated-after-save"
        let! loaded = store.LoadAsync("grok", "issuer|client|domain|url", "work", CancellationToken.None)
        Assert.Equal("access-one", loaded.AccessToken)
        loaded.AccessToken <- "mutated-after-load"
        let! loadedAgain = store.LoadAsync("grok", "issuer|client|domain|url", "work", CancellationToken.None)
        Assert.Equal("access-one", loadedAgain.AccessToken)

        let! otherAccount = store.LoadAsync("grok", "issuer|client|domain|url", "personal", CancellationToken.None)
        let! otherAuthority = store.LoadAsync("grok", "other-authority", "work", CancellationToken.None)
        Assert.Null(otherAccount)
        Assert.Null(otherAuthority)

        do! store.DeleteAsync("grok", "issuer|client|domain|url", "work", CancellationToken.None)
        let! removed = store.LoadAsync("grok", "issuer|client|domain|url", "work", CancellationToken.None)
        Assert.Null(removed)
    }

    [<Fact>]
    member _.``different provider IDs remain isolated in shared store``() = task {
        let store = InMemoryProviderAuthSessionStore() :> IProviderAuthSessionStore
        let session = ProviderAuthSession("github-copilot", "github", "alice")
        session.CredentialKind <- ProviderCredentialKind.Copilot
        session.AccessToken <- "gho-secret"
        do! store.SaveAsync(session, CancellationToken.None)
        let! same = store.LoadAsync("github-copilot", "github", "alice", CancellationToken.None)
        let! other = store.LoadAsync("grok", "github", "alice", CancellationToken.None)
        Assert.Equal(ProviderCredentialKind.Copilot, same.CredentialKind)
        Assert.Equal("gho-secret", same.AccessToken)
        Assert.Null(other)
    }

    [<Fact>]
    member _.``browser callback validation does not consume a valid login and success cannot replay``() = task {
        use handler =
            { new HttpMessageHandler() with
                override _.SendAsync(request, ct) = task {
                    ct.ThrowIfCancellationRequested()
                    return new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent("""{"key":"openrouter-key"}""", Encoding.UTF8, "application/json"))
                } }
        use http = new HttpClient(handler)
        let opts = ProviderAuthOptions.OpenRouter()
        let client = new ProviderAuthClient(opts, InMemoryProviderAuthSessionStore(), http)
        let! login = client.BeginBrowserLoginAsync(CancellationToken.None)
        let bad = UriBuilder(login.RedirectUri)
        bad.Query <- "state=wrong&code=auth-code"
        let! mismatch = Assert.ThrowsAsync<InvalidOperationException>(fun () -> client.CompleteBrowserLoginAsync(login, bad.Uri.ToString(), "work", CancellationToken.None) :> Task)
        Assert.Contains("state", mismatch.Message)

        let good = UriBuilder(login.RedirectUri)
        good.Query <- "state=" + Uri.EscapeDataString(login.State) + "&code=auth-code"
        let! session = client.CompleteBrowserLoginAsync(login, good.Uri.ToString(), "work", CancellationToken.None)
        Assert.Equal("openrouter-key", session.AccessToken)
        let! replay = Assert.ThrowsAsync<InvalidOperationException>(fun () -> client.CompleteBrowserLoginAsync(login, good.Uri.ToString(), "work", CancellationToken.None) :> Task)
        Assert.Contains("already", replay.Message)
    }

    [<Fact>]
    member _.``refresh is serialized across clients sharing a store and keeps rotated token``() = task {
        let mutable requestCount = 0
        use handler =
            { new HttpMessageHandler() with
                override _.SendAsync(request, ct) = task {
                    Interlocked.Increment(&requestCount) |> ignore
                    do! Task.Delay(25, ct)
                    return new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent("""{"access_token":"fresh-access","refresh_token":"rotated-refresh","expires_in":3600}""", Encoding.UTF8, "application/json"))
                } }
        use http = new HttpClient(handler)
        let opts = ProviderAuthOptions.KimiCode(true)
        let store = InMemoryProviderAuthSessionStore() :> IProviderAuthSessionStore
        let stale = ProviderAuthSession(opts.ProviderId, opts.Authority, "work")
        stale.AccessToken <- "stale-access"
        stale.RefreshToken <- "old-refresh"
        stale.ExpiresAt <- Nullable(DateTimeOffset.UtcNow.AddMinutes(-5.))
        do! store.SaveAsync(stale, CancellationToken.None)
        let first = new ProviderAuthClient(opts, store, http)
        let second = new ProviderAuthClient(opts, store, http)
        let! sessions = Task.WhenAll([| first.GetSessionAsync("work", CancellationToken.None); second.GetSessionAsync("work", CancellationToken.None) |])
        Assert.Equal(1, requestCount)
        Assert.All(sessions, fun value -> Assert.Equal("fresh-access", value.AccessToken))
        let! saved = store.LoadAsync(opts.ProviderId, opts.Authority, "work", CancellationToken.None)
        Assert.Equal("rotated-refresh", saved.RefreshToken)
    }

    [<Fact>]
    member _.``invalid grant deletes session and cancellation reaches refresh HTTP``() = task {
        use handler =
            { new HttpMessageHandler() with
                override _.SendAsync(request, ct) = task {
                    ct.ThrowIfCancellationRequested()
                    return new HttpResponseMessage(HttpStatusCode.BadRequest, Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json"))
                } }
        use http = new HttpClient(handler)
        let opts = ProviderAuthOptions.KimiCode(true)
        let store = InMemoryProviderAuthSessionStore() :> IProviderAuthSessionStore
        let stale = ProviderAuthSession(opts.ProviderId, opts.Authority, "work")
        stale.AccessToken <- "stale-access"
        stale.RefreshToken <- "revoked-refresh"
        stale.ExpiresAt <- Nullable(DateTimeOffset.UtcNow.AddMinutes(-5.))
        do! store.SaveAsync(stale, CancellationToken.None)
        let client = new ProviderAuthClient(opts, store, http)
        let! failure = Assert.ThrowsAsync<AiException>(fun () -> client.GetSessionAsync("work", CancellationToken.None) :> Task)
        Assert.Contains("invalid_grant", failure.Message)
        let! removed = store.LoadAsync(opts.ProviderId, opts.Authority, "work", CancellationToken.None)
        Assert.Null(removed)

        let canceledSession = ProviderAuthSession(opts.ProviderId, opts.Authority, "cancelled")
        canceledSession.AccessToken <- "old"
        canceledSession.RefreshToken <- "refresh"
        canceledSession.ExpiresAt <- Nullable(DateTimeOffset.UtcNow.AddMinutes(-5.))
        do! store.SaveAsync(canceledSession, CancellationToken.None)
        let reached = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        use cancelHandler =
            { new HttpMessageHandler() with
                override _.SendAsync(_request, ct) = task {
                    reached.TrySetResult(()) |> ignore
                    do! Task.Delay(Timeout.InfiniteTimeSpan, ct)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                } }
        use cancelHttp = new HttpClient(cancelHandler)
        let cancelClient = new ProviderAuthClient(opts, store, cancelHttp)
        use source = new CancellationTokenSource()
        let pending = cancelClient.GetSessionAsync("cancelled", source.Token)
        do! reached.Task.WaitAsync(TimeSpan.FromSeconds 5.)
        source.Cancel()
        let! canceled = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> pending :> Task)
        Assert.NotNull(canceled)
        let! stillStored = store.LoadAsync(opts.ProviderId, opts.Authority, "cancelled", CancellationToken.None)
        Assert.NotNull(stillStored)
    }

    [<Fact>]
    member _.``device authorization is client scoped single use and observes cancellation``() = task {
        use handler =
            { new HttpMessageHandler() with
                override _.SendAsync(_request, ct) = task {
                    ct.ThrowIfCancellationRequested()
                    return new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent("""{"device_code":"device","user_code":"ABCD","verification_uri":"https://auth.kimi.ai/device","expires_in":600,"interval":5}""", Encoding.UTF8, "application/json"))
                } }
        use http = new HttpClient(handler)
        let options = ProviderAuthOptions.KimiCode(true)
        let client = new ProviderAuthClient(options, InMemoryProviderAuthSessionStore(), http)
        let! device = client.BeginDeviceLoginAsync(CancellationToken.None)
        use canceledSource = new CancellationTokenSource()
        canceledSource.Cancel()
        let! canceled = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> client.CompleteDeviceLoginAsync(device, "work", canceledSource.Token) :> Task)
        Assert.NotNull(canceled)
        let! replay = Assert.ThrowsAsync<InvalidOperationException>(fun () -> client.CompleteDeviceLoginAsync(device, "work", CancellationToken.None) :> Task)
        Assert.Contains("already", replay.Message)
    }

    [<Fact>]
    member _.``loopback browser login receives callback and completes with offline token stub``() = task {
        use portReservation = new TcpListener(System.Net.IPAddress.Loopback, 0)
        portReservation.Start()
        let port = (portReservation.LocalEndpoint :?> System.Net.IPEndPoint).Port
        portReservation.Stop()

        let options = ProviderAuthOptions.OpenRouter()
        options.RedirectUri <- $"http://localhost:{port}/oauth-callback"
        let store = InMemoryProviderAuthSessionStore() :> IProviderAuthSessionStore
        use tokenHandler =
            { new HttpMessageHandler() with
                override _.SendAsync(request, ct) = task {
                    ct.ThrowIfCancellationRequested()
                    Assert.Equal(HttpMethod.Post, request.Method)
                    let! body = request.Content.ReadAsStringAsync(ct)
                    use payload = System.Text.Json.JsonDocument.Parse(body)
                    Assert.Equal("local-auth-code", payload.RootElement.GetProperty("code").GetString())
                    Assert.Equal("S256", payload.RootElement.GetProperty("code_challenge_method").GetString())
                    return new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent("""{"key":"loopback-openrouter-key"}""", Encoding.UTF8, "application/json"))
                } }
        use tokenHttp = new HttpClient(tokenHandler)
        use loopbackHttp = new HttpClient()
        let client = new ProviderAuthClient(options, store, tokenHttp)
        let mutable callbackStatus = HttpStatusCode.InternalServerError
        let openBrowser = Func<Uri, CancellationToken, Task>(fun authorizationUri ct ->
            task {
                Assert.Equal("https", authorizationUri.Scheme)
                let state =
                    authorizationUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun pair -> pair.Split('=', 2))
                    |> Array.tryPick (fun pair -> if pair.[0] = "state" && pair.Length = 2 then Some(Uri.UnescapeDataString(pair.[1])) else None)
                    |> Option.defaultWith (fun () -> failwith "authorization URL omitted state")
                let callback = UriBuilder(options.RedirectUri)
                callback.Query <- "state=" + Uri.EscapeDataString(state) + "&code=local-auth-code"
                use! response = loopbackHttp.GetAsync(callback.Uri, ct)
                callbackStatus <- response.StatusCode
            } :> Task)
        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 5.)
        let! session = client.LoginWithBrowserAsync(openBrowser, "loopback-account", timeout.Token)
        Assert.Equal(HttpStatusCode.OK, callbackStatus)
        Assert.Equal("loopback-openrouter-key", session.AccessToken)
        let! saved = store.LoadAsync("openrouter", options.Authority, "loopback-account", CancellationToken.None)
        Assert.Equal("loopback-openrouter-key", saved.AccessToken)
    }
