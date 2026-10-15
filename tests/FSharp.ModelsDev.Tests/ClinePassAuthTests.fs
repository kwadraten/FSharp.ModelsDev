namespace FSharp.ModelsDev.Tests

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.ModelsDev
open FSharp.ModelsDev.Providers

type private ClinePassStubHandler(send: HttpRequestMessage -> int -> Task<HttpResponseMessage>) =
    inherit HttpMessageHandler()
    let mutable calls = 0
    member _.Calls = calls
    override _.SendAsync(request, _ct) = send request (Interlocked.Increment(&calls))

type private ClinePassTestClock(now: DateTimeOffset) =
    inherit TimeProvider()
    override _.GetUtcNow() = now

type ClinePassAuthTests() =
    let makeOptions () =
        let options = ProviderAuthOptions.ClinePass()
        options.DeviceAuthorizationEndpoint <- "https://workos.test/user_management/authorize/device"
        options.TokenEndpoint <- "https://workos.test/user_management/authenticate"
        options.ApiBaseUrl <- "https://cline.test/api/v1"
        options.TrustedOrigins <- [| "https://workos.test"; "https://cline.test" |]
        options

    let fixedNow = DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero)

    [<Fact>]
    member _.``device login registers raw WorkOS tokens then refreshes prefixed Cline credentials``() = task {
        let forms = ResizeArray<string>()
        let mutable registrationJson = ""
        let mutable refreshJson = ""
        use handler = new ClinePassStubHandler(fun request call -> task {
            if request.Content <> null then
                let! content = request.Content.ReadAsStringAsync()
                if request.Content.Headers.ContentType.MediaType = "application/x-www-form-urlencoded" then forms.Add(content)
                elif call = 4 then registrationJson <- content
                elif call = 5 then refreshJson <- content
            let body, status =
                match call with
                | 1 -> """{"device_code":"device-code","user_code":"X7YZ","verification_uri":"https://workos.test/activate","verification_uri_complete":"https://workos.test/activate?code=X7YZ","expires_in":600,"interval":1}""", HttpStatusCode.OK
                | 2 -> """{"error":"authorization_pending"}""", HttpStatusCode.BadRequest
                | 3 -> """{"access_token":"raw-workos-access","refresh_token":"raw-workos-refresh"}""", HttpStatusCode.OK
                | 4 -> """{"success":true,"data":{"accessToken":"cline-access","refreshToken":"cline-refresh","expiresAt":"2026-10-15T01:00:00Z"}}""", HttpStatusCode.OK
                | 5 -> """{"data":{"accessToken":"refreshed-access","refreshToken":"refreshed-refresh"}}""", HttpStatusCode.OK
                | _ -> """{"data":[{"id":"cline-exact-id","display_name":"Cline Label","context_length":65536,"supports_tool_use":true,"supports_reasoning":true,"reasoning_efforts":["low","server-max"]}]}""", HttpStatusCode.OK
            return new HttpResponseMessage(status, Content = new StringContent(body, Encoding.UTF8, "application/json"))
        })
        use http = new HttpClient(handler, false)
        let clock = ClinePassTestClock(fixedNow)
        let adapter = ClinePassAuthAdapter(makeOptions(), http, clock) :> IProviderAuthAdapter
        let! device = adapter.BeginDeviceLoginAsync(CancellationToken.None)
        Assert.Equal("X7YZ", device.UserCode)
        let! poll = adapter.PollDeviceLoginAsync(device, "account-a", CancellationToken.None)
        Assert.Equal(DevicePollResult.Pending, poll)
        let! poll = adapter.PollDeviceLoginAsync(device, "account-a", CancellationToken.None)
        let session = match poll with DevicePollResult.Authorized value -> value | _ -> failwith "Expected ClinePass authorization"
        Assert.Equal("workos:cline-access", session.AccessToken)
        Assert.Equal("cline-refresh", session.RefreshToken)
        Assert.Equal(DateTimeOffset(2026, 10, 15, 1, 0, 0, TimeSpan.Zero), session.ExpiresAt.Value)
        use registration = JsonDocument.Parse(registrationJson)
        Assert.Equal("raw-workos-access", registration.RootElement.GetProperty("accessToken").GetString())
        Assert.Equal("raw-workos-refresh", registration.RootElement.GetProperty("refreshToken").GetString())
        Assert.DoesNotContain("workos:", registrationJson)
        let deviceForm = System.Web.HttpUtility.ParseQueryString(forms[0])
        Assert.Equal("client_01K3A541FN8TA3EPPHTD2325AR", deviceForm["client_id"])
        Assert.Equal(1, deviceForm.Count)
        let pollForm = System.Web.HttpUtility.ParseQueryString(forms[1])
        Assert.Equal("device-code", pollForm["device_code"])
        Assert.Equal("client_01K3A541FN8TA3EPPHTD2325AR", pollForm["client_id"])
        Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", pollForm["grant_type"])

        let! refreshed = adapter.RefreshAsync(session, CancellationToken.None)
        Assert.Equal("workos:refreshed-access", refreshed.AccessToken)
        Assert.Equal("refreshed-refresh", refreshed.RefreshToken)
        Assert.False(refreshed.ExpiresAt.HasValue)
        use refreshBody = JsonDocument.Parse(refreshJson)
        Assert.Equal("refresh_token", refreshBody.RootElement.GetProperty("granttype").GetString())
        Assert.Equal("cline-refresh", refreshBody.RootElement.GetProperty("refreshToken").GetString())

        let! models = adapter.DiscoverModelsAsync(refreshed, CancellationToken.None)
        let model = Assert.Single(models)
        Assert.Equal("cline-exact-id", model.ModelId)
        Assert.Equal("Cline Label", model.ModelInfo.DisplayName)
        Assert.Equal(65536L, model.ModelInfo.ContextLimit.Value)
        Assert.True(model.ModelInfo.SupportsTools)
        Assert.True(model.ModelInfo.SupportsReasoning)
        Assert.Equal<string[]>([| "low"; "server-max" |], Seq.toArray model.ReasoningEfforts)
        Assert.Equal(6, handler.Calls)
    }

    [<Fact>]
    member _.``WorkOS device denial is returned without registering tokens``() = task {
        use handler = new ClinePassStubHandler(fun _ call ->
            let body = if call = 1 then """{"device_code":"device-code","user_code":"X7YZ","verification_uri":"https://workos.test/activate","expires_in":300}""" else """{"error":"access_denied","error_description":"private detail"}"""
            Task.FromResult(new HttpResponseMessage((if call = 1 then HttpStatusCode.OK else HttpStatusCode.BadRequest), Content = new StringContent(body, Encoding.UTF8, "application/json"))))
        use http = new HttpClient(handler, false)
        let adapter = ClinePassAuthAdapter(makeOptions(), http, ClinePassTestClock(fixedNow)) :> IProviderAuthAdapter
        let! device = adapter.BeginDeviceLoginAsync(CancellationToken.None)
        let! result = adapter.PollDeviceLoginAsync(device, "account-a", CancellationToken.None)
        Assert.Equal(DevicePollResult.Denied, result)
        Assert.Equal(2, handler.Calls)
    }

    [<Fact>]
    member _.``malformed authenticated model catalogs fail instead of producing empty entitlements``() = task {
        use handler = new ClinePassStubHandler(fun _ _ -> Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent("""{"unexpected":[]}""", Encoding.UTF8, "application/json"))))
        use http = new HttpClient(handler, false)
        let adapter = ClinePassAuthAdapter(makeOptions(), http, ClinePassTestClock(fixedNow)) :> IProviderAuthAdapter
        let session = ProviderAuthSession("cline-pass", "authority", "account-a")
        session.AccessToken <- "workos:access"
        session.ApiBaseUrl <- "https://cline.test/api/v1"
        let! error = Assert.ThrowsAsync<AiException>(fun () -> adapter.DiscoverModelsAsync(session, CancellationToken.None) :> Task)
        Assert.Contains("models array", error.Message)
    }

    [<Fact>]
    member _.``public ClinePass client completes device authorization and stores an inference credential``() = task {
        let rawExpiry = DateTimeOffset.UtcNow.AddHours(1.).ToString("O")
        use handler = new ClinePassStubHandler(fun _ call ->
            let body =
                match call with
                | 1 -> """{"device_code":"public-device","user_code":"PUBL","verification_uri":"https://workos.test/activate","expires_in":30,"interval":1}"""
                | 2 -> """{"access_token":"raw-workos","refresh_token":"raw-refresh"}"""
                | _ -> $"""{{"success":true,"data":{{"accessToken":"public-cline","refreshToken":"public-refresh","expiresAt":"{rawExpiry}"}}}}"""
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent(body, Encoding.UTF8, "application/json"))))
        use http = new HttpClient(handler, false)
        use client = new ProviderAuthClient(makeOptions(), InMemoryProviderAuthSessionStore(), http, TimeProvider.System)
        let! device = client.BeginDeviceLoginAsync(CancellationToken.None)
        let! session = client.CompleteDeviceLoginAsync(device, "account-public", CancellationToken.None)
        Assert.Equal("workos:public-cline", session.AccessToken)
        let credentials = client.CreateCredentialProvider()
        let! headers = credentials.GetHeaders("account-public", CancellationToken.None)
        Assert.Equal("Bearer workos:public-cline", headers["Authorization"])
        Assert.Equal(3, handler.Calls)
    }
