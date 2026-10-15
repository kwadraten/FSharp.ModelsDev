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
open FSharp.ModelsDev.Catalog
open FSharp.ModelsDev.Providers

type private KimiStubHandler(send: HttpRequestMessage -> int -> Task<HttpResponseMessage>) =
    inherit HttpMessageHandler()
    let mutable calls = 0
    member _.Calls = calls
    override _.SendAsync(request, _ct) =
        let n = Interlocked.Increment(&calls)
        send request n

type private KimiTestClock(now: DateTimeOffset) =
    inherit TimeProvider()
    override _.GetUtcNow() = now

type private KimiSessionCredentials(session: ProviderAuthSession) =
    interface ICredentialProvider with
        member _.GetHeaders(_accountId, ct) =
            ct.ThrowIfCancellationRequested()
            let headers = Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            headers["Authorization"] <- "Bearer " + session.AccessToken
            Task.FromResult(headers :> IReadOnlyDictionary<string,string>)
    interface IProviderAuthSessionProvider with
        member _.GetSessionAsync(_accountId, ct) = ct.ThrowIfCancellationRequested(); Task.FromResult(session)

type KimiCodeAuthTests() =
    [<Fact>]
    member _.``device OAuth refresh and catalog use only advertised Kimi values``() = task {
        let seenForms = ResizeArray<string>()
        let mutable deviceHeader = ""
        let mutable userAgent = ""
        let mutable mshPlatform = ""
        let mutable mshVersion = ""
        use handler = new KimiStubHandler(fun request call -> task {
            deviceHeader <- request.Headers.GetValues("X-Msh-Device-Id") |> Seq.head
            userAgent <- request.Headers.GetValues("User-Agent") |> Seq.head
            mshPlatform <- request.Headers.GetValues("X-Msh-Platform") |> Seq.head
            mshVersion <- request.Headers.GetValues("X-Msh-Version") |> Seq.head
            if request.Method = HttpMethod.Post then
                let! form = request.Content.ReadAsStringAsync()
                seenForms.Add(form)
            let body =
                match call with
                | 1 -> """{"device_code":"device-1","user_code":"ABCD","verification_uri":"https://kimi.test/verify","verification_uri_complete":"https://kimi.test/verify?code=ABCD","expires_in":600,"interval":1}"""
                | 2 -> """{"error":"authorization_pending","error_description":"wait"}"""
                | 3 -> """{"access_token":"access-1","refresh_token":"refresh-1","expires_in":3600}"""
                | 4 -> """{"access_token":"access-2","refresh_token":"refresh-2","expires_in":7200}"""
                | _ -> """{"data":[{"id":"k3-256k","display_name":"K3 (256K)","context_length":262144,"supports_reasoning":true,"supports_tool_use":true,"supports_thinking_type":"both","think_efforts":{"support":true,"valid_efforts":["low","custom-max"]}}]}"""
            let status = if call = 2 then HttpStatusCode.BadRequest else HttpStatusCode.OK
            return new HttpResponseMessage(status, Content = new StringContent(body, Encoding.UTF8, "application/json"))
        })
        use http = new HttpClient(handler, false)
        let options = ProviderAuthOptions.KimiCode(true)
        options.DeviceAuthorizationEndpoint <- "https://auth.test/device_authorization"
        options.TokenEndpoint <- "https://auth.test/token"
        options.ApiBaseUrl <- "https://api.test/coding/v1"
        options.DeviceId <- "persisted-device"
        options.TrustedOrigins <- [| "https://auth.test"; "https://api.test" |]
        let adapter = KimiCodeAuthAdapter(options, http, KimiTestClock(DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero))) :> IProviderAuthAdapter
        let! device = adapter.BeginDeviceLoginAsync(CancellationToken.None)
        Assert.Equal("ABCD", device.UserCode)
        let! pending = adapter.PollDeviceLoginAsync(device, "account-1", CancellationToken.None)
        Assert.Equal(DevicePollResult.Pending, pending)
        let! authorized = adapter.PollDeviceLoginAsync(device, "account-1", CancellationToken.None)
        let session = match authorized with DevicePollResult.Authorized value -> value | _ -> failwith "Expected device authorization"
        Assert.Equal("persisted-device", session.DeviceId)
        Assert.Equal("https://api.test/coding/v1", session.ApiBaseUrl)
        let! refreshed = adapter.RefreshAsync(session, CancellationToken.None)
        Assert.Equal("access-2", refreshed.AccessToken)
        Assert.Equal("persisted-device", refreshed.DeviceId)
        let! models = adapter.DiscoverModelsAsync(refreshed, CancellationToken.None)
        let model = Assert.Single(models)
        Assert.Equal("kimi-code", model.ProtocolId)
        Assert.Equal<string[]>([| "messages" |], Seq.toArray model.SupportedEndpoints)
        Assert.Equal("k3-256k", model.ModelId)
        Assert.Equal("K3 (256K)", model.ModelInfo.DisplayName)
        Assert.Equal(262144L, model.ModelInfo.ContextLimit.Value)
        Assert.True(model.SupportsThinkingToggle)
        Assert.False(model.AlwaysThinking)
        Assert.Equal<string[]>([| "low"; "custom-max" |], Seq.toArray model.ReasoningEfforts)
        Assert.Equal("persisted-device", deviceHeader)
        Assert.Equal("KimiCLI/1.49.0", userAgent)
        Assert.Equal("kimi_cli", mshPlatform)
        Assert.Equal("1.49.0", mshVersion)
        Assert.Contains("client_id=17e5f671-d194-4dfb-9706-5516cb48c098", seenForms[0])
        Assert.DoesNotContain("scope=", seenForms[0])
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", seenForms[1])
        Assert.Contains("grant_type=refresh_token", seenForms[3])
        Assert.Equal(5, handler.Calls)
    }

    [<Fact>]
    member _.``backend sends exact discovered effort identity and session cache key``() = task {
        let descriptor = ProviderModelDescriptor("kimi-code-plan-global", "exact-server-id", "kimi-code", "https://api.test/coding/v1")
        descriptor.ReasoningEfforts <- [| "low"; "server-max" |]
        descriptor.ModelInfo <- ModelInfo("kimi-code-plan-global", "exact-server-id", "Server Label", "exact-server-id", ModelCapabilities.TextInput ||| ModelCapabilities.Reasoning, Nullable 32768L, Nullable<int64>(), descriptor.ReasoningEfforts)
        let session = ProviderAuthSession("kimi-code-plan-global", "authority", "acct")
        session.AccessToken <- "token"
        session.DeviceId <- "stable-device-id"
        let options = ProviderOptions()
        options.DiscoveredModel <- descriptor
        options.SessionId <- "session-cache-id"
        let backend = KimiCodeBackend("exact-server-id", "acct", KimiSessionCredentials(session) :> ICredentialProvider, options) :> IChatBackend
        let request = ChatRequest("exact-server-id", [| ChatMessage.User("hello") |])
        request.ReasoningEffort <- "server-max"
        let! prepared = backend.PrepareRequest(request, false, CancellationToken.None)
        use document = JsonDocument.Parse(prepared.Body)
        let root = document.RootElement
        Assert.Equal("exact-server-id", root.GetProperty("model").GetString())
        Assert.Equal("enabled", root.GetProperty("thinking").GetProperty("type").GetString())
        Assert.Equal("server-max", root.GetProperty("reasoning_effort").GetString())
        Assert.Equal("session-cache-id", root.GetProperty("prompt_cache_key").GetString())
        Assert.Equal("Bearer token", prepared.Headers["Authorization"])
        Assert.Equal("stable-device-id", prepared.Headers["X-Msh-Device-Id"])
        Assert.Equal("KimiCLI/1.49.0", prepared.Headers["User-Agent"])
        Assert.Equal("kimi_cli", prepared.Headers["X-Msh-Platform"])
        Assert.Equal("1.49.0", prepared.Headers["X-Msh-Version"])
        Assert.False(String.IsNullOrWhiteSpace prepared.Headers["X-Msh-Device-Name"])
        Assert.False(String.IsNullOrWhiteSpace prepared.Headers["X-Msh-Device-Model"])
        Assert.False(String.IsNullOrWhiteSpace prepared.Headers["X-Msh-Os-Version"])
        Assert.EndsWith("/messages", prepared.Url)
        Assert.Equal("2023-06-01", prepared.Headers["anthropic-version"])
    }

    [<Fact>]
    member _.``backend rejects invented effort names``() = task {
        let descriptor = ProviderModelDescriptor("kimi-code-plan-global", "selected", "kimi-code", "https://api.test/coding/v1")
        descriptor.ReasoningEfforts <- [| "low" |]
        descriptor.ModelInfo <- ModelInfo("kimi-code-plan-global", "selected", "Selected", "selected", ModelCapabilities.TextInput ||| ModelCapabilities.Reasoning, Nullable<int64>(), Nullable<int64>(), descriptor.ReasoningEfforts)
        let options = ProviderOptions()
        options.DiscoveredModel <- descriptor
        let credentials = { new ICredentialProvider with member _.GetHeaders(_, _) = Task.FromResult(Dictionary<string,string>() :> IReadOnlyDictionary<string,string>) }
        let backend = KimiCodeBackend("selected", "acct", credentials, options) :> IChatBackend
        let request = ChatRequest("selected", [| ChatMessage.User("hello") |])
        request.ReasoningEffort <- "medium"
        let! error = Assert.ThrowsAsync<AiException>(fun () -> backend.PrepareRequest(request, false, CancellationToken.None) :> Task)
        Assert.Contains("not supported", error.Message)
    }

    [<Fact>]
    member _.``Messages tool calls parse as structured calls and preserve state in the next turn``() = task {
        let descriptor = ProviderModelDescriptor("kimi-code-plan-global", "selected", "kimi-code", "https://api.test/coding/v1")
        descriptor.ReasoningEfforts <- [| "low"; "high"; "max" |]
        descriptor.AlwaysThinking <- true
        descriptor.ModelInfo <- ModelInfo("kimi-code-plan-global", "selected", "Selected", "selected", ModelCapabilities.TextInput ||| ModelCapabilities.ToolCalling ||| ModelCapabilities.Reasoning, Nullable<int64>(), Nullable<int64>(), descriptor.ReasoningEfforts)
        let session = ProviderAuthSession("kimi-code-plan-global", "authority", "acct")
        session.AccessToken <- "token"
        session.DeviceId <- "stable-device-id"
        let options = ProviderOptions()
        options.DiscoveredModel <- descriptor
        let backend = KimiCodeBackend("selected", "acct", KimiSessionCredentials(session) :> ICredentialProvider, options) :> IChatBackend
        let response = backend.ParseResponse("""{"id":"msg-1","model":"selected","content":[{"type":"thinking","thinking":"keep this","signature":"sig"},{"type":"tool_use","id":"call-1","name":"lookup","input":{"q":"weather"}}],"stop_reason":"tool_use","usage":{"input_tokens":3,"output_tokens":4}}""")
        Assert.Equal(FinishReason.ToolCalls, response.FinishReason)
        let call = Assert.Single(response.Message.ToolCalls)
        Assert.Equal("lookup", call.Name)
        Assert.Equal("{\"q\":\"weather\"}", call.ArgumentsJson)
        Assert.Contains("signature", response.Message.ProviderStateJson)
        let parser = backend.CreateStreamParser()
        let push eventName payload =
            parser.PushLine("event: " + eventName) |> ignore
            parser.PushLine("data: " + payload) |> ignore
            parser.PushLine("")
        push "content_block_start" """{"index":0,"content_block":{"type":"thinking","thinking":"keep this","signature":"sig"}}""" |> ignore
        push "content_block_start" """{"index":1,"content_block":{"type":"tool_use","id":"call-stream","name":"lookup","input":{}}}""" |> ignore
        push "content_block_delta" """{"index":1,"delta":{"type":"input_json_delta","partial_json":"{\"q\":\"weather\"}"}}""" |> ignore
        push "content_block_stop" """{"index":1}""" |> ignore
        push "message_delta" """{"delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":4}}""" |> ignore
        let terminal = push "message_stop" "{}"
        let state = terminal |> Seq.tryPick (function :? ProviderStateUpdate as update -> Some update.StateJson | _ -> None) |> Option.defaultWith (fun () -> failwith "Expected provider state")
        Assert.Contains("signature", state)
        let assistant = ChatMessage(Role.Assistant, [| TextPart("checking") |])
        assistant.ProviderStateJson <- state
        let request = ChatRequest("selected", [| ChatMessage.User("look up weather"); assistant; ChatMessage.ToolResult("call-stream", "sunny") |])
        let! prepared = backend.PrepareRequest(request, false, CancellationToken.None)
        use body = JsonDocument.Parse(prepared.Body)
        let messages = body.RootElement.GetProperty("messages")
        let assistantBlocks = messages[1].GetProperty("content")
        let resultBlocks = messages[2].GetProperty("content")
        Assert.Equal("tool_use", assistantBlocks[1].GetProperty("type").GetString())
        Assert.Equal("tool_result", resultBlocks[0].GetProperty("type").GetString())
        Assert.Equal("enabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString())
    }

    [<Fact>]
    member _.``OpenAI Chat protocol aliases remain explicit compatibility choices``() = task {
        for protocolId in [ "openai-chat"; "openai-compatible" ] do
            let session = ProviderAuthSession("kimi-code-plan-global", "authority", "acct")
            session.AccessToken <- "token"
            let options = ProviderOptions()
            options.ProtocolId <- protocolId
            options.BaseUrl <- "https://api.test/coding/v1"
            let backend = KimiCodeBackend("selected", "acct", KimiSessionCredentials(session) :> ICredentialProvider, options) :> IChatBackend
            let! prepared = backend.PrepareRequest(ChatRequest("selected", [| ChatMessage.User("hello") |]), false, CancellationToken.None)
            Assert.EndsWith("/chat/completions", prepared.Url)
    }
