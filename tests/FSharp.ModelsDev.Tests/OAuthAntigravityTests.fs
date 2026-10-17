namespace FSharp.ModelsDev.Tests

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.ModelsDev
open FSharp.ModelsDev.Providers

type private StubHandler(send: HttpRequestMessage -> int -> Task<HttpResponseMessage>) =
    inherit HttpMessageHandler()
    let mutable calls = 0
    member _.Calls = calls
    override _.SendAsync(request, ct) =
        let n = Interlocked.Increment(&calls)
        send request n

type private MutableClock(now: DateTimeOffset) =
    inherit TimeProvider()
    let mutable current = now
    member _.Advance(duration: TimeSpan) = current <- current.Add duration
    override _.GetUtcNow() = current

type private SessionCredentials(session: GoogleOAuthSession) =
    let headers = Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
    member _.Headers = headers
    interface IGoogleOAuthSessionProvider with
        member _.GetSessionAsync(_accountId, ct) = ct.ThrowIfCancellationRequested(); Task.FromResult session
        member _.Invalidate(_accountId) = ()
    interface ICredentialProvider with
        member _.GetHeaders(_accountId, ct) =
            ct.ThrowIfCancellationRequested()
            headers["Authorization"] <- "Bearer " + session.AccessToken
            Task.FromResult(headers :> IReadOnlyDictionary<string,string>)

type private HeaderOnlyCredentials() =
    interface ICredentialProvider with
        member _.GetHeaders(_accountId, ct) =
            ct.ThrowIfCancellationRequested()
            Task.FromResult(Dictionary<string,string>() :> IReadOnlyDictionary<string,string>)

type private CustomHeaderCredentials() =
    interface ICredentialProvider with
        member _.GetHeaders(_accountId, ct) =
            ct.ThrowIfCancellationRequested()
            let headers = Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            headers["Authorization"] <- "Bearer access"
            headers["X-Trace"] <- "trace-123"
            Task.FromResult(headers :> IReadOnlyDictionary<string,string>)

type OAuthAntigravityTests() =
    [<Fact>]
    member _.``OAuth client credentials have no built in defaults``() =
        let options = GoogleOAuthOptions()
        Assert.Null(options.ClientId)
        Assert.Null(options.ClientSecret)
        use client = new GoogleOAuthClient(options, InMemoryOAuthTokenStore())
        let error = Assert.Throws<ArgumentException>(fun () -> client.BeginLogin() |> ignore)
        Assert.Equal("ClientId", error.ParamName)

    [<Fact>]
    member _.``refresh requires explicit client id before network access``() = task {
        let handler = new StubHandler(fun _ _ -> failwith "Token endpoint must not be called")
        use http = new HttpClient(handler)
        let store = InMemoryOAuthTokenStore() :> IOAuthTokenStore
        do! store.SaveAsync(GoogleOAuthSession("acct", "expired", "refresh", DateTimeOffset.UtcNow.AddHours(-1.), null), CancellationToken.None)
        use provider = new GoogleOAuthCredentialProvider(GoogleOAuthOptions(), store, http)
        let! error = Assert.ThrowsAsync<ArgumentException>(fun () ->
            (provider :> IGoogleOAuthSessionProvider).GetSessionAsync("acct", CancellationToken.None) :> Task)
        Assert.Equal("ClientId", error.ParamName)
        Assert.Equal(0, handler.Calls)
    }

    [<Theory>]
    [<InlineData(null)>]
    [<InlineData("test-client-secret")>]
    member _.``refresh sends configured client id and only includes a supplied secret``(secret: string) = task {
        let handler = new StubHandler(fun request _ -> task {
            let! body = request.Content.ReadAsStringAsync()
            let form = System.Web.HttpUtility.ParseQueryString(body)
            Assert.Equal("test-client-id", form["client_id"])
            Assert.Equal(secret, form["client_secret"])
            return new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent("""{"access_token":"fresh","expires_in":3600}""", Encoding.UTF8, "application/json"))
        })
        use http = new HttpClient(handler)
        let store = InMemoryOAuthTokenStore() :> IOAuthTokenStore
        do! store.SaveAsync(GoogleOAuthSession("acct", "expired", "refresh", DateTimeOffset.UtcNow.AddHours(-1.), null), CancellationToken.None)
        let options = GoogleOAuthOptions(ClientId = "test-client-id", ClientSecret = secret)
        use provider = new GoogleOAuthCredentialProvider(options, store, http)
        let! session = (provider :> IGoogleOAuthSessionProvider).GetSessionAsync("acct", CancellationToken.None)
        Assert.Equal("fresh", session.AccessToken)
        Assert.Equal(1, handler.Calls)
    }

    [<Fact>]
    member _.``authorization URL uses distinct state and S256 PKCE``() =
        let options = GoogleOAuthOptions(ClientId = "test-client-id", ClientSecret = "test-client-secret")
        options.AuthorizationEndpoint <- "https://login.test/authorize"
        let client = new GoogleOAuthClient(options, InMemoryOAuthTokenStore())
        let login = client.BeginLogin()
        let query = System.Web.HttpUtility.ParseQueryString(login.AuthorizationUrl.Query)
        Assert.Equal("S256", query["code_challenge_method"])
        Assert.Equal("offline", query["access_type"])
        Assert.Equal("consent", query["prompt"])
        Assert.False(String.IsNullOrWhiteSpace query["state"])
        Assert.False(String.IsNullOrWhiteSpace query["code_challenge"])
        Assert.NotEqual<string>(query["state"], query["code_challenge"])
        let expectedChallenge =
            Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes login.Verifier))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_')
        Assert.Equal(expectedChallenge, query["code_challenge"])

    [<Fact>]
    member _.``callback state and redirect are validated before exchange``() =
        let handler = new StubHandler(fun _ _ -> failwith "Token endpoint must not be called")
        use http = new HttpClient(handler)
        let options = GoogleOAuthOptions(ClientId = "test-client-id", ClientSecret = "test-client-secret")
        options.TokenEndpoint <- "https://oauth.test/token"
        let client = new GoogleOAuthClient(options, InMemoryOAuthTokenStore(), http, null)
        let login = client.BeginLogin()
        let state = Uri.UnescapeDataString(login.AuthorizationUrl.Query.Split("state=").[1].Split('&').[0])
        let wrongState = Uri(options.RedirectUri + "?code=c&state=wrong")
        Assert.Throws<AiException>(fun () -> client.CompleteLoginAsync(login, wrongState.ToString(), "acct", CancellationToken.None).GetAwaiter().GetResult() |> ignore) |> ignore
        Assert.Equal(0, handler.Calls)
        let replay = Uri(options.RedirectUri + "?code=c&state=" + Uri.EscapeDataString state)
        Assert.Throws<AiException>(fun () -> client.CompleteLoginAsync(login, replay.ToString(), "acct", CancellationToken.None).GetAwaiter().GetResult() |> ignore) |> ignore

    [<Fact>]
    member _.``login expires after five minutes``() =
        let clock = MutableClock(DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero))
        let options = GoogleOAuthOptions(ClientId = "test-client-id", ClientSecret = "test-client-secret")
        options.TimeProvider <- clock
        let client = new GoogleOAuthClient(options, InMemoryOAuthTokenStore(), null, clock)
        let login = client.BeginLogin()
        clock.Advance(TimeSpan.FromMinutes 5. |> fun x -> x.Add(TimeSpan.FromSeconds 1.))
        let callback = Uri(options.RedirectUri + "?code=c&state=none")
        Assert.Throws<AiException>(fun () -> client.CompleteLoginAsync(login, callback.ToString(), "acct", CancellationToken.None).GetAwaiter().GetResult() |> ignore) |> ignore

    [<Fact>]
    member _.``concurrent refreshes are single flight and preserve rotated refresh token``() = task {
        let handler = new StubHandler(fun _ _ -> task {
            do! Task.Delay 40
            return new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent("{\"access_token\":\"fresh\",\"expires_in\":3600}", Encoding.UTF8, "application/json"))
        })
        use http = new HttpClient(handler)
        let store = InMemoryOAuthTokenStore()
        let tokenStore = store :> IOAuthTokenStore
        do! tokenStore.SaveAsync(GoogleOAuthSession("acct", "old", "keep-refresh", DateTimeOffset.UtcNow.AddMinutes(-1.), "project-a"), CancellationToken.None)
        let options = GoogleOAuthOptions(ClientId = "test-client-id", ClientSecret = "test-client-secret")
        options.TokenEndpoint <- "https://oauth.test/token"
        let credentials = new GoogleOAuthCredentialProvider(options, store, http, null)
        let sessions =
            [| (credentials :> IGoogleOAuthSessionProvider).GetSessionAsync("acct", CancellationToken.None)
               (credentials :> IGoogleOAuthSessionProvider).GetSessionAsync("acct", CancellationToken.None) |]
        let! loaded = Task.WhenAll sessions
        Assert.Equal(1, handler.Calls)
        Assert.All(loaded, fun s -> Assert.Equal("fresh", s.AccessToken); Assert.Equal("keep-refresh", s.RefreshToken); Assert.Equal("project-a", s.ProjectId))
        let! persisted = tokenStore.LoadAsync("acct", CancellationToken.None)
        Assert.Equal("project-a", persisted.ProjectId)
    }

    [<Fact>]
    member _.``invalid grant clears stored credentials and asks account to sign in again``() = task {
        let handler = new StubHandler(fun _ _ -> Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest, Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json"))))
        use http = new HttpClient(handler)
        let tokenStore = InMemoryOAuthTokenStore() :> IOAuthTokenStore
        do! tokenStore.SaveAsync(GoogleOAuthSession("acct", "old", "revoked", DateTimeOffset.UtcNow.AddMinutes(-2.), null), CancellationToken.None)
        let options = GoogleOAuthOptions(ClientId = "test-client-id", ClientSecret = "test-client-secret")
        options.TokenEndpoint <- "https://oauth.test/token"
        let provider = new GoogleOAuthCredentialProvider(options, tokenStore, http)
        let error =
            Assert.Throws<AiException>(fun () ->
                (provider :> IGoogleOAuthSessionProvider).GetSessionAsync("acct", CancellationToken.None).GetAwaiter().GetResult()
                |> ignore)
        Assert.Contains("invalid_grant", error.Message)
        let! cleared = tokenStore.LoadAsync("acct", CancellationToken.None)
        Assert.Null(cleared)
        Assert.Equal(1, handler.Calls)
    }

    [<Fact>]
    member _.``explicit invalidation refreshes a valid token and preserves project and refresh token``() = task {
        let handler = new StubHandler(fun request _ -> task {
            let! form = request.Content.ReadAsStringAsync()
            Assert.Contains("grant_type=refresh_token", form)
            return new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent("{\"access_token\":\"rotated-access\",\"expires_in\":3600}", Encoding.UTF8, "application/json"))
        })
        use http = new HttpClient(handler)
        let tokenStore = InMemoryOAuthTokenStore() :> IOAuthTokenStore
        do! tokenStore.SaveAsync(GoogleOAuthSession("acct", "still-valid", "unchanged-refresh", DateTimeOffset.UtcNow.AddHours(1.), "project-kept"), CancellationToken.None)
        let options = GoogleOAuthOptions(ClientId = "test-client-id", ClientSecret = "test-client-secret")
        options.TokenEndpoint <- "https://oauth.test/token"
        let provider = new GoogleOAuthCredentialProvider(options, tokenStore, http)
        let sessions = provider :> IGoogleOAuthSessionProvider
        sessions.Invalidate("acct")
        let! refreshed = sessions.GetSessionAsync("acct", CancellationToken.None)
        Assert.Equal("rotated-access", refreshed.AccessToken)
        Assert.Equal("unchanged-refresh", refreshed.RefreshToken)
        Assert.Equal("project-kept", refreshed.ProjectId)
        let! persisted = tokenStore.LoadAsync("acct", CancellationToken.None)
        Assert.Equal("unchanged-refresh", persisted.RefreshToken)
        Assert.Equal("project-kept", persisted.ProjectId)
        Assert.Equal(1, handler.Calls)
    }

    [<Fact>]
    member _.``Antigravity discovers project once and wraps Gemini request``() = task {
        let handler = new StubHandler(fun request _ -> task {
            Assert.Equal(HttpMethod.Post, request.Method)
            Assert.Contains("Bearer access", request.Headers.Authorization.ToString())
            Assert.Contains("trace-123", request.Headers.GetValues("X-Trace"))
            Assert.Contains("configured-extra", request.Headers.GetValues("X-Integration"))
            if request.RequestUri.AbsolutePath.EndsWith("loadCodeAssist", StringComparison.Ordinal) then
                return new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent("{\"cloudaicompanionProject\":{\"projectId\":\"project-42\"}}", Encoding.UTF8, "application/json"))
            else return failwith "Only project discovery should make an HTTP call during preparation"
        })
        use http = new HttpClient(handler)
        let credentials = CustomHeaderCredentials() :> ICredentialProvider
        let options = ProviderOptions()
        let extraHeaders = Dictionary<string,string>()
        extraHeaders["X-Integration"] <- "configured-extra"
        options.Headers <- extraHeaders :> IReadOnlyDictionary<string,string>
        let backend = new AntigravityBackend("claude-sonnet", "acct", credentials, options, http)
        let request = ChatRequest("claude-sonnet", [| ChatMessage.User "hello" |])
        let! prepared = (backend :> IChatBackend).PrepareRequest(request, false, CancellationToken.None)
        Assert.Equal("https://cloudcode-pa.googleapis.com/v1internal:generateContent", prepared.Url)
        use doc = JsonDocument.Parse prepared.Body
        Assert.Equal("claude-sonnet", doc.RootElement.GetProperty("model").GetString())
        Assert.Equal("project-42", doc.RootElement.GetProperty("project").GetString())
        Assert.Equal("antigravity", doc.RootElement.GetProperty("userAgent").GetString())
        Assert.Equal("hello", doc.RootElement.GetProperty("request").GetProperty("contents").[0].GetProperty("parts").[0].GetProperty("text").GetString())
        Assert.Equal("ANTIGRAVITY", JsonDocument.Parse(prepared.Headers["Client-Metadata"]).RootElement.GetProperty("ideType").GetString())
        Assert.Equal("trace-123", prepared.Headers["X-Trace"])
        Assert.Equal(1, handler.Calls)
    }

    [<Fact>]
    member _.``configured project skips discovery and stream uses SSE endpoint``() = task {
        let handler = new StubHandler(fun _ _ -> failwith "Project discovery should be skipped")
        use http = new HttpClient(handler)
        let credentials = SessionCredentials(GoogleOAuthSession("acct", "access", "refresh", DateTimeOffset.UtcNow.AddHours(1.), "stored-project")) :> ICredentialProvider
        let options = ProviderOptions()
        options.ProjectId <- "chosen-project"
        let backend = new AntigravityBackend("gemini-2.5-pro", "acct", credentials, options, http)
        let! prepared = (backend :> IChatBackend).PrepareRequest(ChatRequest("gemini-2.5-pro", [| ChatMessage.User "x" |]), true, CancellationToken.None)
        Assert.Equal("https://cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse", prepared.Url)
        use doc = JsonDocument.Parse prepared.Body
        Assert.Equal("chosen-project", doc.RootElement.GetProperty("project").GetString())
        Assert.Equal(0, handler.Calls)
    }

    [<Fact>]
    member _.``Antigravity accepts string project discovery response``() = task {
        let handler = new StubHandler(fun _ _ -> Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent("{\"cloudaicompanionProject\":\"project-string\"}", Encoding.UTF8, "application/json"))))
        use http = new HttpClient(handler)
        let credentials = SessionCredentials(GoogleOAuthSession("acct", "access", "refresh", DateTimeOffset.UtcNow.AddHours(1.), null)) :> ICredentialProvider
        let backend = new AntigravityBackend("gemini-2.5-flash", "acct", credentials, ProviderOptions(), http)
        let! prepared = (backend :> IChatBackend).PrepareRequest(ChatRequest("gemini-2.5-flash", [| ChatMessage.User "hello" |]), false, CancellationToken.None)
        use doc = JsonDocument.Parse prepared.Body
        Assert.Equal("project-string", doc.RootElement.GetProperty("project").GetString())
    }

    [<Fact>]
    member _.``missing discovered project asks for explicit project id``() = task {
        let handler = new StubHandler(fun _ _ -> Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent("{}", Encoding.UTF8, "application/json"))))
        use http = new HttpClient(handler)
        let credentials = SessionCredentials(GoogleOAuthSession("acct", "access", "refresh", DateTimeOffset.UtcNow.AddHours(1.), null)) :> ICredentialProvider
        let backend = new AntigravityBackend("gemini-2.5-flash", "acct", credentials, ProviderOptions(), http)
        let error =
            Assert.Throws<AiException>(fun () ->
                (backend :> IChatBackend).PrepareRequest(ChatRequest("gemini-2.5-flash", [| ChatMessage.User "hello" |]), false, CancellationToken.None).GetAwaiter().GetResult()
                |> ignore)
        Assert.Contains("ProviderOptions.ProjectId", error.Message)
    }

    [<Fact>]
    member _.``missing bearer token fails even when project is configured``() = task {
        let backendOptions = ProviderOptions()
        backendOptions.ProjectId <- "configured-project"
        use http = new HttpClient()
        let backend = new AntigravityBackend("gemini-2.5-flash", "acct", HeaderOnlyCredentials(), backendOptions, http)
        let error =
            Assert.Throws<AiException>(fun () ->
                (backend :> IChatBackend).PrepareRequest(ChatRequest("gemini-2.5-flash", [| ChatMessage.User "hello" |]), false, CancellationToken.None).GetAwaiter().GetResult()
                |> ignore)
        Assert.Contains("bearer token", error.Message)
        (backend :> IDisposable).Dispose()
    }

    [<Fact>]
    member _.``request scoped backend uses the requested model in its envelope``() = task {
        use http = new HttpClient(new StubHandler(fun _ _ -> failwith "Configured project should skip discovery"))
        let credentials = SessionCredentials(GoogleOAuthSession("acct", "access", "refresh", DateTimeOffset.UtcNow.AddHours(1.), "stored-project")) :> ICredentialProvider
        let backend = new AntigravityBackend("gemini-2.5-pro", "acct", credentials, ProviderOptions(), http)
        let scoped = backend :> IRequestScopedBackend
        let requested = scoped.ResolveBackend("claude-sonnet-4")
        let! prepared = requested.PrepareRequest(ChatRequest("claude-sonnet-4", [| ChatMessage.User "hello" |]), false, CancellationToken.None)
        use doc = JsonDocument.Parse prepared.Body
        Assert.Equal("claude-sonnet-4", doc.RootElement.GetProperty("model").GetString())
    }

    [<Fact>]
    member _.``loopback login binds root prefix and accepts the configured callback path``() = task {
        use portProbe = new TcpListener(IPAddress.Loopback, 0)
        portProbe.Start()
        let port = (portProbe.LocalEndpoint :?> IPEndPoint).Port
        portProbe.Stop()
        let redirect = $"http://localhost:{port}/oauth-callback"
        let options = GoogleOAuthOptions(ClientId = "test-client-id", ClientSecret = "test-client-secret")
        options.RedirectUri <- redirect
        options.TokenEndpoint <- "https://oauth.test/token"
        let mutable exchangedForm: string = null
        let handler = new StubHandler(fun request _ -> task {
            let! form = request.Content.ReadAsStringAsync()
            exchangedForm <- form
            Assert.Contains("grant_type=authorization_code", form)
            return new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent("{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}", Encoding.UTF8, "application/json"))
        })
        use http = new HttpClient(handler)
        let store = InMemoryOAuthTokenStore()
        let tokenStore = store :> IOAuthTokenStore
        let client = new GoogleOAuthClient(options, tokenStore, http)
        let mutable authorizationUrl: Uri = null
        let launcher = Func<Uri, CancellationToken, Task>(fun (authUri: Uri) (ct: CancellationToken) -> (task {
            authorizationUrl <- authUri
            let authQuery = System.Web.HttpUtility.ParseQueryString(authUri.Query)
            use callbackClient = new HttpClient()
            let callback = redirect + "?code=login-code&state=" + Uri.EscapeDataString(authQuery["state"])
            use! response = callbackClient.GetAsync(callback, ct)
            response.EnsureSuccessStatusCode() |> ignore
        } :> Task))
        let! session = client.LoginAsync(launcher, "loopback-account", CancellationToken.None)
        Assert.Equal("access", session.AccessToken)
        let! stored = tokenStore.LoadAsync("loopback-account", CancellationToken.None)
        Assert.Equal("refresh", stored.RefreshToken)
        Assert.Equal(1, handler.Calls)
        let form = System.Web.HttpUtility.ParseQueryString exchangedForm
        let authorization = System.Web.HttpUtility.ParseQueryString authorizationUrl.Query
        let verifier = form["code_verifier"]
        let actualChallenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes verifier)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        Assert.Equal(authorization["code_challenge"], actualChallenge)
    }

    [<Fact>]
    member _.``canceling loopback login releases the listener promptly``() = task {
        use portProbe = new TcpListener(IPAddress.Loopback, 0)
        portProbe.Start()
        let port = (portProbe.LocalEndpoint :?> IPEndPoint).Port
        portProbe.Stop()
        let redirect = $"http://localhost:{port}/oauth-callback"
        let options = GoogleOAuthOptions(ClientId = "test-client-id", ClientSecret = "test-client-secret")
        options.RedirectUri <- redirect
        let client = new GoogleOAuthClient(options, InMemoryOAuthTokenStore())
        use cancellation = new CancellationTokenSource(250)
        let launcher = Func<Uri, CancellationToken, Task>(fun _ _ -> Task.CompletedTask)
        let mutable canceled = false
        try
            let! _ = client.LoginAsync(launcher, "cancelled-account", cancellation.Token)
            ()
        with :? OperationCanceledException -> canceled <- true
        Assert.True(canceled)
        use listener = new HttpListener()
        listener.Prefixes.Add($"http://localhost:{port}/")
        listener.Start()
        listener.Stop()
        (client :> IDisposable).Dispose()
    }

    [<Fact>]
    member _.``Antigravity discovery uses subscription IDs and does not populate offline models`` () = task {
        let session = GoogleOAuthSession("account", "fictional-token", null, DateTimeOffset.UtcNow.AddHours(1.), "account-project")
        let credentials = SessionCredentials(session)
        let handler = new StubHandler(fun request _ -> task {
            Assert.Equal("https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels", request.RequestUri.AbsoluteUri)
            Assert.Equal(HttpMethod.Post, request.Method)
            Assert.Equal("Bearer fictional-token", request.Headers.Authorization.ToString())
            let! body = request.Content.ReadAsStringAsync()
            use doc = JsonDocument.Parse(body)
            Assert.Equal("account-project", doc.RootElement.GetProperty("project").GetString())
            return new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent("""{"models":{"subscription-model-b":{},"subscription-model-a":{"displayName":"My model"},"internal-model":{"isInternal":true}}}"""))
        })
        use http = new HttpClient(handler)
        let discovery = AntigravityModelDiscovery(credentials, ProviderOptions(), http)
        Assert.Equal(0, handler.Calls)
        let! models = discovery.DiscoverModelsAsync("account")
        Assert.Equal<string>([| "subscription-model-a"; "subscription-model-b" |], models |> Seq.map (fun m -> m.ModelId) |> Seq.toArray)
        for model in models do
            Assert.Equal("antigravity", model.ProtocolId)
            Assert.Equal("account", model.AccountId)
            Assert.Null(model.ModelInfo)
        Assert.Empty(FSharp.ModelsDev.Catalog.SnapshotCatalog.Default.GetModels("antigravity"))
        // The caller still owns the injected client.
        Assert.NotNull(http.DefaultRequestHeaders)
    }

    [<Theory>]
    [<InlineData("{}")>]
    [<InlineData("{\"models\":[]}")>]
    [<InlineData("{invalid secret-token")>]
    member _.``Antigravity discovery rejects malformed responses without exposing them`` (body: string) = task {
        let session = GoogleOAuthSession("account", "fictional-token", null, DateTimeOffset.UtcNow.AddHours(1.), "project")
        let handler = new StubHandler(fun _ _ -> Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent(body))))
        use http = new HttpClient(handler)
        let discovery = AntigravityModelDiscovery(SessionCredentials(session), ProviderOptions(), http)
        let! error = Assert.ThrowsAsync<AiException>(fun () -> discovery.DiscoverModelsAsync("account") :> Task)
        Assert.DoesNotContain("secret-token", error.Message)
    }

    [<Theory>]
    [<InlineData(401)>]
    [<InlineData(403)>]
    member _.``Antigravity discovery preserves HTTP status without leaking response body`` (status: int) = task {
        let session = GoogleOAuthSession("account", "fictional-token", null, DateTimeOffset.UtcNow.AddHours(1.), "project")
        let handler = new StubHandler(fun _ _ -> Task.FromResult(new HttpResponseMessage(enum<HttpStatusCode> status, Content = new StringContent("secret-token"))))
        use http = new HttpClient(handler)
        let discovery = AntigravityModelDiscovery(SessionCredentials(session), ProviderOptions(), http)
        let! error = Assert.ThrowsAsync<AiException>(fun () -> discovery.DiscoverModelsAsync("account") :> Task)
        Assert.DoesNotContain("secret-token", error.Message)
        Assert.Equal(1, handler.Calls)
    }

    [<Fact>]
    member _.``Antigravity discovery propagates cancellation before requesting credentials`` () = task {
        let handler = new StubHandler(fun _ _ -> failwith "No network expected")
        use http = new HttpClient(handler)
        let discovery = AntigravityModelDiscovery(HeaderOnlyCredentials(), ProviderOptions(), http)
        use cts = new CancellationTokenSource()
        cts.Cancel()
        let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> discovery.DiscoverModelsAsync("account", cts.Token) :> Task)
        Assert.Equal(0, handler.Calls)
    }
