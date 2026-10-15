namespace FSharp.ModelsDev.Tests

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.ModelsDev
open FSharp.ModelsDev.Providers
open FSharp.ModelsDev.Catalog

type private CapturedRequest =
    { Method: string
      Url: string
      Authorization: string
      UserAgent: string
      EditorVersion: string
      Content: string }

type private StubHttpHandler(send: Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>) =
    inherit HttpMessageHandler()
    override _.SendAsync(request, cancellationToken) = send.Invoke(request, cancellationToken)

module private GitHubCopilotTestHelpers =
    let response body = new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent(body, Encoding.UTF8, "application/json"))
    let capture (request: HttpRequestMessage) ct = task {
        let! content = if isNull request.Content then Task.FromResult("") else request.Content.ReadAsStringAsync(ct)
        let auth = if isNull request.Headers.Authorization then "" else request.Headers.Authorization.ToString()
        let ua = request.Headers.UserAgent.ToString()
        let editor = match request.Headers.TryGetValues("Editor-Version") with true, values -> String.Join(" ", values) | _ -> ""
        return
            { Method = request.Method.Method
              Url = request.RequestUri.AbsoluteUri
              Authorization = auth
              UserAgent = ua
              EditorVersion = editor
              Content = content }
    }
    let handler (requests: ResizeArray<CapturedRequest>) (makeResponse: CapturedRequest -> HttpResponseMessage) =
        new StubHttpHandler(Func<_,_,_>(fun request ct -> task {
            let! captured = capture request ct
            requests.Add captured
            return makeResponse captured
        }))

type GitHubCopilotAuthTests() =
    [<Fact>]
    member _.``device authorization stores upstream token and exchanges it for a Copilot token``() = task {
        let requests = ResizeArray<CapturedRequest>()
        use handler = GitHubCopilotTestHelpers.handler requests (fun captured ->
            if captured.Url.EndsWith("/login/device/code", StringComparison.Ordinal) then
                GitHubCopilotTestHelpers.response """{"device_code":"device-secret","user_code":"ABCD-EFGH","verification_uri":"https://github.com/login/device","expires_in":900,"interval":2}"""
            elif captured.Url.EndsWith("/login/oauth/access_token", StringComparison.Ordinal) then
                GitHubCopilotTestHelpers.response """{"access_token":"gho-upstream-secret"}"""
            else
                GitHubCopilotTestHelpers.response """{"token":"ghu-copilot-secret","expires_at":1790000000,"endpoints":{"api":"https://api.githubcopilot.com"}}"""
        )
        use http = new HttpClient(handler)
        let options = ProviderAuthOptions.GitHubCopilot()
        let adapter = GitHubCopilotAuthAdapter(options, http, TimeProvider.System) :> IProviderAuthAdapter
        let! device = adapter.BeginDeviceLoginAsync(CancellationToken.None)
        Assert.Equal("ABCD-EFGH", device.UserCode)
        let! polled = adapter.PollDeviceLoginAsync(device, "work", CancellationToken.None)
        let upstream =
            match polled with
            | DevicePollResult.Authorized session -> session
            | other -> failwithf "Expected an authorized session, got %A" other
        Assert.Equal("gho-upstream-secret", upstream.UpstreamToken)
        Assert.Null(upstream.AccessToken)

        let! refreshed = adapter.RefreshAsync(upstream, CancellationToken.None)
        Assert.Equal("gho-upstream-secret", refreshed.UpstreamToken)
        Assert.Equal("ghu-copilot-secret", refreshed.AccessToken)
        Assert.True(refreshed.ExpiresAt.HasValue)
        Assert.Equal(3, requests.Count)
        Assert.Contains("grant_type", requests.[1].Content)
        Assert.Equal("Bearer gho-upstream-secret", requests.[2].Authorization)
        Assert.Equal("GitHubCopilotChat/0.35.0", requests.[2].UserAgent)
        Assert.Equal("vscode/1.107.0", requests.[2].EditorVersion)
    }

    [<Fact>]
    member _.``enterprise domain derives token exchange host``() = task {
        let requests = ResizeArray<CapturedRequest>()
        use handler = GitHubCopilotTestHelpers.handler requests (fun captured ->
            if captured.Url.EndsWith("/login/device/code", StringComparison.Ordinal) then
                GitHubCopilotTestHelpers.response """{"device_code":"device","user_code":"ABCD","verification_uri":"https://company.ghe.com/login/device","expires_in":900,"interval":2}"""
            elif captured.Url.EndsWith("/login/oauth/access_token", StringComparison.Ordinal) then
                GitHubCopilotTestHelpers.response """{"access_token":"upstream"}"""
            else GitHubCopilotTestHelpers.response """{"token":"copilot-token","expires_at":1790000000,"endpoints":{"api":"https://copilot-api.company.ghe.com"}}"""
        )
        use http = new HttpClient(handler)
        let options = ProviderAuthOptions.GitHubCopilot()
        options.EnterpriseDomain <- "company.ghe.com"
        let adapter = GitHubCopilotAuthAdapter(options, http, TimeProvider.System) :> IProviderAuthAdapter
        let! device = adapter.BeginDeviceLoginAsync(CancellationToken.None)
        Assert.StartsWith("https://company.ghe.com/", requests.[0].Url)
        let! polled = adapter.PollDeviceLoginAsync(device, "enterprise", CancellationToken.None)
        let upstream = match polled with DevicePollResult.Authorized value -> value | _ -> failwith "device flow did not authorize"
        let! _ = adapter.RefreshAsync(upstream, CancellationToken.None)
        Assert.StartsWith("https://api.company.ghe.com/copilot_internal/v2/token", requests.[2].Url)
    }

    [<Fact>]
    member _.``custom token endpoint must use a trusted HTTPS origin``() = task {
        let requests = ResizeArray<CapturedRequest>()
        use handler = GitHubCopilotTestHelpers.handler requests (fun _ -> GitHubCopilotTestHelpers.response "{}")
        use http = new HttpClient(handler)
        let options = ProviderAuthOptions.GitHubCopilot()
        options.CopilotTokenEndpoint <- "https://attacker.example/token"
        let adapter = GitHubCopilotAuthAdapter(options, http, TimeProvider.System) :> IProviderAuthAdapter
        let session = ProviderAuthSession("github-copilot", options.Authority, "alice")
        session.UpstreamToken <- "upstream"
        let! error = Assert.ThrowsAsync<AiException>(fun () -> adapter.RefreshAsync(session, CancellationToken.None) :> Task)
        Assert.Contains("trusted origins", error.Message)
        Assert.Empty(requests)
    }

    [<Fact>]
    member _.``model discovery prefers responses and preserves endpoint capabilities``() = task {
        let requests = ResizeArray<CapturedRequest>()
        use handler = GitHubCopilotTestHelpers.handler requests (fun _ ->
            GitHubCopilotTestHelpers.response """{"data":[{"id":"model-z","name":"Model Z","supported_endpoints":["/messages","/chat/completions","/responses"],"input_modalities":["text","image"],"context_length":128000,"max_output_tokens":8192,"reasoning_efforts":["low","high"],"capabilities":{"supports_tools":true,"supports_reasoning":true,"supports_thinking_toggle":true}}]}"""
        )
        use http = new HttpClient(handler)
        let options = ProviderAuthOptions.GitHubCopilot()
        let adapter = GitHubCopilotAuthAdapter(options, http, TimeProvider.System) :> IProviderAuthAdapter
        let session = ProviderAuthSession("github-copilot", options.Authority, "alice")
        session.AccessToken <- "copilot-token"
        session.ApiBaseUrl <- options.ApiBaseUrl
        let! models = adapter.DiscoverModelsAsync(session, CancellationToken.None)
        Assert.Single(models) |> ignore
        let model = models.[0]
        Assert.Equal("openai-responses", model.ProtocolId)
        Assert.Equal("alice", model.AccountId)
        Assert.Equal<string>("/messages", model.SupportedEndpoints.[0])
        Assert.Equal<string>("low", model.ReasoningEfforts.[0])
        Assert.True(model.SupportsThinkingToggle)
        Assert.True(model.ModelInfo.Capabilities.HasFlag ModelCapabilities.ImageInput)
        Assert.True(model.ModelInfo.Capabilities.HasFlag ModelCapabilities.ToolCalling)
        Assert.Equal("Bearer copilot-token", requests.[0].Authorization)
    }
