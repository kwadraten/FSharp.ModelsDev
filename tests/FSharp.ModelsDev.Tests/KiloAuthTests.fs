namespace FSharp.ModelsDev.Tests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.ModelsDev
open FSharp.ModelsDev.Providers

type private KiloStubHandler(send: HttpRequestMessage -> Task<HttpResponseMessage>) =
    inherit HttpMessageHandler()
    let mutable calls = 0
    member _.Calls = calls
    override _.SendAsync(request, _ct) =
        Interlocked.Increment(&calls) |> ignore
        send request

module private KiloTestHelpers =
    let json status body = new HttpResponseMessage(status, Content = new StringContent(body, Encoding.UTF8, "application/json"))
    let startResponse () = json HttpStatusCode.OK """{"code":"device-code-1","verificationUrl":"https://app.kilo.ai/device","expiresIn":900}"""

open KiloTestHelpers

type KiloAuthTests() =
    [<Fact>]
    member _.``device authorization posts to current endpoint and pending then approval preserves org without fabricated expiry``() = task {
        let mutable polls = 0
        let handler = new KiloStubHandler(fun request -> task {
            match request.Method, request.RequestUri.AbsolutePath with
            | m, "/api/device-auth/codes" when m = HttpMethod.Post ->
                Assert.Equal("api.kilo.ai", request.RequestUri.Host)
                return startResponse()
            | m, "/api/device-auth/codes/device-code-1" when m = HttpMethod.Get ->
                polls <- polls + 1
                if polls = 1 then return json (enum<HttpStatusCode> 202) "{}"
                else return json HttpStatusCode.OK """{"status":"approved","token":"opaque-kilo-token","userEmail":"user@example.test"}"""
            | _ -> return failwithf "Unexpected Kilo request %O" request.RequestUri
        })
        use http = new HttpClient(handler)
        let options = ProviderAuthOptions.Kilo()
        options.OrganizationId <- "org-123"
        let adapter = KiloAuthAdapter(options, http, TimeProvider.System) :> IProviderAuthAdapter
        let! device = adapter.BeginDeviceLoginAsync(CancellationToken.None)
        Assert.Equal("device-code-1", device.UserCode)
        let! pending = adapter.PollDeviceLoginAsync(device, "acct", CancellationToken.None)
        Assert.Equal(DevicePollResult.Pending, pending)
        let! result = adapter.PollDeviceLoginAsync(device, "acct", CancellationToken.None)
        match result with
        | DevicePollResult.Authorized session ->
            Assert.Equal("opaque-kilo-token", session.AccessToken)
            Assert.Equal(ProviderCredentialKind.OAuth, session.CredentialKind)
            Assert.Equal("org-123", session.OrganizationId)
            Assert.False(session.ExpiresAt.HasValue)
        | _ -> Assert.Fail("expected authorized device login")
        Assert.Equal("https://api.kilo.ai/api/gateway", options.ApiBaseUrl)
    }

    [<Theory>]
    [<InlineData(403, 3)>]
    [<InlineData(410, 4)>]
    member _.``device poll maps denied and expired HTTP statuses``(status: int, expected: int) = task {
        let handler = new KiloStubHandler(fun request -> task {
            if request.Method = HttpMethod.Post then return startResponse()
            else return json (enum<HttpStatusCode> status) "{}"
        })
        use http = new HttpClient(handler)
        let adapter = KiloAuthAdapter(ProviderAuthOptions.Kilo(), http, TimeProvider.System) :> IProviderAuthAdapter
        let! device = adapter.BeginDeviceLoginAsync(CancellationToken.None)
        let! result = adapter.PollDeviceLoginAsync(device, "acct", CancellationToken.None)
        match expected, result with
        | 3, DevicePollResult.Denied -> ()
        | 4, DevicePollResult.Expired -> ()
        | _ -> Assert.Fail($"unexpected poll result {result}")
    }

    [<Fact>]
    member _.``model discovery uses gateway endpoint and organization headers``() = task {
        let mutable receivedEditor = ""
        let mutable receivedOrganization = ""
        let handler = new KiloStubHandler(fun request -> task {
            if request.Method = HttpMethod.Post then return startResponse()
            elif request.RequestUri.AbsolutePath = "/api/device-auth/codes/device-code-1" then
                return json HttpStatusCode.OK """{"status":"approved","token":"opaque-token"}"""
            else
                Assert.Equal("/api/gateway/models", request.RequestUri.AbsolutePath)
                Assert.Equal("Bearer opaque-token", request.Headers.Authorization.ToString())
                receivedEditor <- request.Headers.GetValues("X-KILOCODE-EDITORNAME") |> Seq.head
                receivedOrganization <- request.Headers.GetValues("X-KILOCODE-ORGANIZATIONID") |> Seq.head
                return json HttpStatusCode.OK """{"data":[{"id":"vendor/model-v2","name":"Model V2","context_length":32768,"architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},"supported_parameters":["tools","reasoning","structured_outputs"]}]}"""
        })
        use http = new HttpClient(handler)
        let options = ProviderAuthOptions.Kilo()
        options.OrganizationId <- "org-for-models"
        let adapter = KiloAuthAdapter(options, http, TimeProvider.System) :> IProviderAuthAdapter
        let! device = adapter.BeginDeviceLoginAsync(CancellationToken.None)
        let! poll = adapter.PollDeviceLoginAsync(device, "acct", CancellationToken.None)
        let session = match poll with DevicePollResult.Authorized value -> value | _ -> failwith "expected approval"
        let! models = adapter.DiscoverModelsAsync(session, CancellationToken.None)
        Assert.Equal("FSharp.ModelsDev", receivedEditor)
        Assert.Equal("org-for-models", receivedOrganization)
        Assert.Single(models) |> ignore
        Assert.Equal("vendor/model-v2", models[0].ModelInfo.ModelId)
        Assert.Equal("acct", models[0].AccountId)
        Assert.True(models[0].ModelInfo.Capabilities.HasFlag(FSharp.ModelsDev.Catalog.ModelCapabilities.ImageInput))
        Assert.True(models[0].ModelInfo.Capabilities.HasFlag(FSharp.ModelsDev.Catalog.ModelCapabilities.ToolCalling))
        Assert.True(models[0].ModelInfo.Capabilities.HasFlag(FSharp.ModelsDev.Catalog.ModelCapabilities.Reasoning))
        Assert.True(models[0].ModelInfo.Capabilities.HasFlag(FSharp.ModelsDev.Catalog.ModelCapabilities.StructuredOutput))
    }

    [<Fact>]
    member _.``approved device response without a token fails safely``() = task {
        let handler = new KiloStubHandler(fun request -> task {
            if request.Method = HttpMethod.Post then return startResponse()
            else return json HttpStatusCode.OK """{"status":"approved","userEmail":"user@example.test"}"""
        })
        use http = new HttpClient(handler)
        let adapter = KiloAuthAdapter(ProviderAuthOptions.Kilo(), http, TimeProvider.System) :> IProviderAuthAdapter
        let! device = adapter.BeginDeviceLoginAsync(CancellationToken.None)
        let! error = Assert.ThrowsAsync<AiException>(fun () -> adapter.PollDeviceLoginAsync(device, "acct", CancellationToken.None) :> Task)
        Assert.Contains("did not contain an access token", error.Message)
    }
