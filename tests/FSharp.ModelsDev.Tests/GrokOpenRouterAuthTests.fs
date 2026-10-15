namespace FSharp.ModelsDev.Tests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.ModelsDev
open FSharp.ModelsDev.Providers

type private AuthStubHandler(send: HttpRequestMessage -> Task<HttpResponseMessage>) =
    inherit HttpMessageHandler()
    let mutable calls = 0
    member _.Calls = calls
    override _.SendAsync(request, ct) =
        Interlocked.Increment(&calls) |> ignore
        send request

type private AuthTestClock(now: DateTimeOffset) =
    inherit TimeProvider()
    override _.GetUtcNow() = now

module private AuthTestHelpers =
    let response status body =
        new HttpResponseMessage(status, Content = new StringContent(body, Encoding.UTF8, "application/json"))

open AuthTestHelpers

type GrokOpenRouterAuthTests() =
    [<Fact>]
    member _.``Grok browser PKCE discovers trusted issuer and exchanges code on subscription proxy``() = task {
        let mutable tokenForm = ""
        let handler = new AuthStubHandler(fun request -> task {
            if request.RequestUri.AbsoluteUri = "https://auth.x.ai/.well-known/openid-configuration" then
                return response HttpStatusCode.OK """{"issuer":"https://auth.x.ai","authorization_endpoint":"https://auth.x.ai/oauth2/authorize","token_endpoint":"https://auth.x.ai/oauth2/token","device_authorization_endpoint":"https://auth.x.ai/oauth2/device/code"}"""
            elif request.RequestUri.AbsolutePath = "/oauth2/token" then
                let! form = request.Content.ReadAsStringAsync()
                tokenForm <- form
                return response HttpStatusCode.OK """{"access_token":"grok-access","refresh_token":"grok-refresh","expires_in":3600}"""
            else return failwithf "Unexpected Grok request %O" request.RequestUri
        })
        use http = new HttpClient(handler)
        let options = ProviderAuthOptions.Grok()
        let adapter = GrokAuthAdapter(options, http, AuthTestClock(DateTimeOffset(2026,10,15,0,0,0,TimeSpan.Zero))) :> IProviderAuthAdapter
        let! login = adapter.BeginBrowserLoginAsync(CancellationToken.None)
        Assert.Equal("https://cli-chat-proxy.grok.com/v1", options.ApiBaseUrl)
        let query = System.Web.HttpUtility.ParseQueryString(login.AuthorizationUrl.Query)
        Assert.Equal("S256", query["code_challenge_method"])
        Assert.False(String.IsNullOrWhiteSpace query["state"])
        Assert.Contains("grok-cli%3Aaccess", login.AuthorizationUrl.Query)
        let! session = adapter.ExchangeBrowserCodeAsync(login, "one-time-code", "account-a", CancellationToken.None)
        Assert.Equal("grok-access", session.AccessToken)
        Assert.Equal("grok-refresh", session.RefreshToken)
        Assert.Equal("account-a", session.AccountId)
        Assert.Contains("grant_type=authorization_code", tokenForm)
        Assert.Contains("code_verifier=", tokenForm)
        Assert.Equal(2, handler.Calls)
    }

    [<Fact>]
    member _.``ProviderAuthClient completes Grok browser login after validating callback once``() = task {
        let handler = new AuthStubHandler(fun request -> task {
            if request.RequestUri.AbsoluteUri = "https://auth.x.ai/.well-known/openid-configuration" then
                return response HttpStatusCode.OK """{"issuer":"https://auth.x.ai","authorization_endpoint":"https://auth.x.ai/oauth2/authorize","token_endpoint":"https://auth.x.ai/oauth2/token"}"""
            elif request.RequestUri.AbsolutePath = "/oauth2/token" then
                return response HttpStatusCode.OK """{"access_token":"grok-public-api-access","refresh_token":"grok-refresh","expires_in":3600}"""
            else return failwithf "Unexpected Grok request %O" request.RequestUri
        })
        use http = new HttpClient(handler)
        let options = ProviderAuthOptions.Grok()
        use client = new ProviderAuthClient(options, InMemoryProviderAuthSessionStore(), http)
        let! login = client.BeginBrowserLoginAsync(CancellationToken.None)
        let state = System.Web.HttpUtility.ParseQueryString(login.AuthorizationUrl.Query)["state"]
        let callback = options.RedirectUri + "?code=browser-code&state=" + Uri.EscapeDataString(state)
        let! session = client.CompleteBrowserLoginAsync(login, callback, "public-account", CancellationToken.None)
        Assert.Equal("grok-public-api-access", session.AccessToken)
        Assert.Equal("public-account", session.AccountId)
        Assert.Equal(2, handler.Calls)
    }

    [<Fact>]
    member _.``Grok OIDC discovery rejects an untrusted token endpoint before contacting it``() = task {
        let handler = new AuthStubHandler(fun request -> task {
            Assert.Equal("https://auth.x.ai/.well-known/openid-configuration", request.RequestUri.AbsoluteUri)
            return response HttpStatusCode.OK """{"issuer":"https://auth.x.ai","authorization_endpoint":"https://auth.x.ai/oauth2/authorize","token_endpoint":"https://evil.example/token"}"""
        })
        use http = new HttpClient(handler)
        let adapter = GrokAuthAdapter(ProviderAuthOptions.Grok(), http, TimeProvider.System) :> IProviderAuthAdapter
        let! _ = Assert.ThrowsAsync<AiException>(fun () -> adapter.BeginBrowserLoginAsync(CancellationToken.None) :> Task)
        Assert.Equal(1, handler.Calls)
    }

    [<Fact>]
    member _.``Grok device polling maps pending and captures exact token request``() = task {
        let mutable pollForm = ""
        let mutable polls = 0
        let handler = new AuthStubHandler(fun request -> task {
            match request.RequestUri.AbsolutePath with
            | "/.well-known/openid-configuration" -> return response HttpStatusCode.OK """{"issuer":"https://auth.x.ai","authorization_endpoint":"https://auth.x.ai/oauth2/authorize","token_endpoint":"https://auth.x.ai/oauth2/token","device_authorization_endpoint":"https://auth.x.ai/oauth2/device/code"}"""
            | "/oauth2/device/code" -> return response HttpStatusCode.OK """{"device_code":"device-secret","user_code":"ABCD-EFGH","verification_uri":"https://auth.x.ai/activate","expires_in":17,"interval":47}"""
            | "/oauth2/token" ->
                let! form = request.Content.ReadAsStringAsync()
                pollForm <- form
                polls <- polls + 1
                if polls = 1 then return response HttpStatusCode.BadRequest """{"error":"authorization_pending"}"""
                else return response HttpStatusCode.OK """{"access_token":"device-access","refresh_token":"device-refresh","expires_in":900}"""
            | path -> return failwithf "Unexpected Grok request %s" path
        })
        use http = new HttpClient(handler)
        let adapter = GrokAuthAdapter(ProviderAuthOptions.Grok(), http, AuthTestClock(DateTimeOffset(2026,10,15,0,0,0,TimeSpan.Zero))) :> IProviderAuthAdapter
        let! device = adapter.BeginDeviceLoginAsync(CancellationToken.None)
        Assert.Equal("ABCD-EFGH", device.UserCode)
        Assert.Equal(TimeSpan.FromSeconds(47.), device.Interval)
        Assert.Equal(DateTimeOffset(2026,10,15,0,0,17,TimeSpan.Zero), device.ExpiresAt)
        let! pending = adapter.PollDeviceLoginAsync(device, "account-b", CancellationToken.None)
        Assert.Equal(DevicePollResult.Pending, pending)
        let! authorized = adapter.PollDeviceLoginAsync(device, "account-b", CancellationToken.None)
        match authorized with
        | DevicePollResult.Authorized session -> Assert.Equal("device-access", session.AccessToken); Assert.Equal("account-b", session.AccountId)
        | _ -> Assert.Fail("expected an authorized session")
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", pollForm)
        Assert.Contains("device_code=device-secret", pollForm)
    }

    [<Fact>]
    member _.``OpenRouter creates S256 URL and exchanges JSON code for an API key without invented expiry``() = task {
        let mutable exchangeBody = ""
        let handler = new AuthStubHandler(fun request -> task {
            if request.RequestUri.AbsolutePath = "/api/v1/auth/keys" then
                let! body = request.Content.ReadAsStringAsync()
                exchangeBody <- body
                return response HttpStatusCode.OK """{"key":"sk-or-user-key"}"""
            elif request.RequestUri.AbsolutePath = "/api/v1/models" then
                Assert.Equal("Bearer", request.Headers.Authorization.Scheme)
                Assert.Equal("sk-or-user-key", request.Headers.Authorization.Parameter)
                return response HttpStatusCode.OK """{"data":[{"id":"openai/gpt-test","name":"GPT Test","context_length":8192,"top_provider":{"max_completion_tokens":2048},"architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},"supported_parameters":["tools","reasoning","structured_outputs"]}]}"""
            else return failwithf "Unexpected OpenRouter request %O" request.RequestUri
        })
        use http = new HttpClient(handler)
        let options = ProviderAuthOptions.OpenRouter()
        options.RedirectUri <- null
        let adapter = OpenRouterAuthAdapter(options, http, AuthTestClock(DateTimeOffset(2026,10,15,0,0,0,TimeSpan.Zero))) :> IProviderAuthAdapter
        let! login = adapter.BeginBrowserLoginAsync(CancellationToken.None)
        let query = System.Web.HttpUtility.ParseQueryString(login.AuthorizationUrl.Query)
        Assert.Equal("S256", query["code_challenge_method"])
        Assert.Null(query["callback_url"])
        Assert.False(String.IsNullOrWhiteSpace query["code_challenge"])
        Assert.False(String.IsNullOrWhiteSpace query["state"])
        let! session = adapter.ExchangeBrowserCodeAsync(login, "or-auth-code", "account-c", CancellationToken.None)
        Assert.Equal(ProviderCredentialKind.ApiKey, session.CredentialKind)
        Assert.Equal("sk-or-user-key", session.AccessToken)
        Assert.False(session.ExpiresAt.HasValue)
        use body = JsonDocument.Parse(exchangeBody)
        Assert.Equal("or-auth-code", body.RootElement.GetProperty("code").GetString())
        Assert.Equal("S256", body.RootElement.GetProperty("code_challenge_method").GetString())
        Assert.False(String.IsNullOrWhiteSpace(body.RootElement.GetProperty("code_verifier").GetString()))
        let! models = adapter.DiscoverModelsAsync(session, CancellationToken.None)
        Assert.Single(models) |> ignore
        Assert.Equal("openai-chat", models[0].ProtocolId)
        Assert.Equal("account-c", models[0].AccountId)
        Assert.True(models[0].ModelInfo.Capabilities.HasFlag(FSharp.ModelsDev.Catalog.ModelCapabilities.ImageInput))
        Assert.True(models[0].ModelInfo.Capabilities.HasFlag(FSharp.ModelsDev.Catalog.ModelCapabilities.ToolCalling))
        Assert.True(models[0].ModelInfo.Capabilities.HasFlag(FSharp.ModelsDev.Catalog.ModelCapabilities.Reasoning))
        Assert.True(models[0].ModelInfo.Capabilities.HasFlag(FSharp.ModelsDev.Catalog.ModelCapabilities.StructuredOutput))
        Assert.Equal(2048L, models[0].ModelInfo.MaxOutputTokens.Value)
    }
