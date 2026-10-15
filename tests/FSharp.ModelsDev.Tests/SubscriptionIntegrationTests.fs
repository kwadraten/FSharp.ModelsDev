namespace FSharp.ModelsDev.Tests

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog
open FSharp.ModelsDev.Providers
open FSharp.ModelsDev.MEAi
open Microsoft.Extensions.AI

type private SubscriptionCredentials(providerId: string, baseUrl: string) =
    let session = ProviderAuthSession(providerId, "test-authority", "account")
    let mutable calls = 0
    let mutable invalidations = 0
    do session.AccessToken <- "subscription-token"; session.ApiBaseUrl <- baseUrl; session.DeviceId <- "own-device"; session.OrganizationId <- "org"
    member _.Calls = calls
    member _.Invalidations = invalidations
    interface IRefreshableCredentialProvider with
        member _.CanRefresh = true
        member _.Invalidate(_) = invalidations <- invalidations + 1
        member _.GetHeaders(_, ct) =
            ct.ThrowIfCancellationRequested()
            calls <- calls + 1
            let headers = Dictionary<string,string>()
            headers["Authorization"] <- "Bearer subscription-token"
            Task.FromResult(headers :> IReadOnlyDictionary<string,string>)
    interface IProviderAuthSessionProvider with
        member _.GetSessionAsync(_, ct) =
            ct.ThrowIfCancellationRequested()
            calls <- calls + 1
            Task.FromResult session

type private SubscriptionTransport(failStatus: int, streamFailureAfterEvent: bool) =
    let mutable calls = 0
    let mutable url = ""
    let mutable headers: IReadOnlyDictionary<string,string> = null
    let mutable body = ""
    let response = """{"id":"r","model":"test-model","choices":[{"message":{"role":"assistant","content":"checking","reasoning_content":"protected-thinking","tool_calls":[{"id":"call","type":"function","function":{"name":"lookup","arguments":"{\"x\":1}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}"""
    let messagesResponse = """{"id":"r","model":"test-model","content":[{"type":"text","text":"checking"},{"type":"thinking","thinking":"protected-thinking","signature":"opaque-signature"},{"type":"tool_use","id":"call","name":"lookup","input":{"x":1}}],"stop_reason":"tool_use","usage":{"input_tokens":2,"output_tokens":3}}"""
    let capture endpoint requestHeaders requestBody =
        calls <- calls + 1
        url <- endpoint
        headers <- requestHeaders
        body <- requestBody
    member _.Calls = calls
    member _.Url = url
    member _.Headers = headers
    member _.Body = body
    interface IChatTransport with
        member _.Post(endpoint, requestHeaders, requestBody, ct) = task {
            ct.ThrowIfCancellationRequested()
            capture endpoint requestHeaders requestBody
            if calls = 1 && failStatus <> 0 then raise (AiException("upstream denied", failStatus))
            return TransportResponse(200, if endpoint.EndsWith("/messages") then messagesResponse else response)
        }
        member _.PostStream(endpoint, requestHeaders, requestBody, ct) =
            AsyncEnumerableBridge.ofProducerWithCancellation ct (fun writer token -> task {
                capture endpoint requestHeaders requestBody
                if calls = 1 && failStatus <> 0 && not streamFailureAfterEvent then raise (AiException("upstream denied", failStatus))
                let chatLines =
                    [| "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"protected-\"}}]}"; ""
                       "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"thinking\",\"content\":\"checking\",\"tool_calls\":[{\"index\":0,\"id\":\"call\",\"function\":{\"name\":\"lookup\",\"arguments\":\"{\\\"x\\\":1}\"}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":3,\"total_tokens\":5}}"; ""
                       "data: [DONE]"; "" |]
                let messagesLines =
                    [| "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":2,\"output_tokens\":0}}}"; ""
                       "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}"; ""
                       "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"checking\"}}"; ""
                       "data: {\"type\":\"content_block_stop\",\"index\":0}"; ""
                       "data: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"protected-thinking\",\"signature\":\"opaque-signature\"}}"; ""
                       "data: {\"type\":\"content_block_stop\",\"index\":1}"; ""
                       "data: {\"type\":\"content_block_start\",\"index\":2,\"content_block\":{\"type\":\"tool_use\",\"id\":\"call\",\"name\":\"lookup\",\"input\":{}}}"; ""
                       "data: {\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"x\\\":1}\"}}"; ""
                       "data: {\"type\":\"content_block_stop\",\"index\":2}"; ""
                       "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":3}}"; ""
                       "data: {\"type\":\"message_stop\"}"; "" |]
                let lines = if endpoint.EndsWith("/messages") then messagesLines else chatLines
                for line in lines do do! writer.WriteAsync(line, token).AsTask()
                if streamFailureAfterEvent then raise (AiException("late unauthorized", 401))
            })

module private SubscriptionIntegration =
    let endpoints provider =
        match provider with
        | "grok" -> "https://cli-chat-proxy.grok.com/v1"
        | "openrouter" -> "https://openrouter.ai/api/v1"
        | "kimi-code-plan-cn" -> "https://api.kimi.com/coding/v1"
        | "github-copilot" -> "https://api.business.githubcopilot.com"
        | "kilo" -> "https://api.kilo.ai/api/gateway"
        | _ -> "https://api.cline.bot/api/v1"
    let factory provider transport =
        let credentials = SubscriptionCredentials(provider, endpoints provider)
        let factory = ProviderFactory(SnapshotCatalog(), credentials, null, transport)
        factory, credentials

type private CodexSubscriptionTransport() =
    let mutable posted = 0
    let mutable streamed = 0
    let mutable body = ""
    let mutable endpoint = ""
    let completed = """{"type":"response.completed","response":{"id":"codex-completed","model":"test-model","status":"completed","output":[{"type":"reasoning","id":"r","encrypted_content":"protected-codex"},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"checking"}]},{"type":"function_call","call_id":"call","name":"lookup","arguments":"{\"x\":1}"}],"usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5}}}"""
    member _.Posted = posted
    member _.Streamed = streamed
    member _.Body = body
    member _.Endpoint = endpoint
    interface IChatTransport with
        member _.Post(_, _, _, _) =
            posted <- posted + 1
            Task.FromException<TransportResponse>(InvalidOperationException("Codex subscription requires streaming."))
        member _.PostStream(url, _, requestBody, ct) =
            endpoint <- url
            body <- requestBody
            streamed <- streamed + 1
            AsyncEnumerableBridge.ofProducerWithCancellation ct (fun writer token -> task {
                do! writer.WriteAsync("data: " + completed, token).AsTask()
                do! writer.WriteAsync("", token).AsTask()
            })

type SubscriptionIntegrationTests() =
    [<Fact>]
    member _.``Codex factory decorators preserve streaming completions and MEAI tool history``() = task {
        let transport = CodexSubscriptionTransport()
        let credentials = SubscriptionCredentials("openai-codex", "https://chatgpt.com/backend-api/codex")
        let factory = ProviderFactory(SnapshotCatalog(), credentials, null, transport)
        let headers = Dictionary<string,string>()
        headers["ChatGPT-Account-Id"] <- "account-id"
        let options = ProviderOptions(Headers = headers)
        let model = factory.CreateClient("openai-codex", "account", "test-model", options)
        Assert.Equal(0, credentials.Calls)
        use client = model.AsIChatClient()
        let user = Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "lookup")
        let! first = client.GetResponseAsync([| user |], null, CancellationToken.None)
        Assert.Equal("codex-completed", first.ResponseId)
        Assert.Equal("checking", first.Text)
        let assistant = Assert.Single(first.Messages)
        let call = Assert.Single(assistant.Contents |> Seq.choose(function :? FunctionCallContent as c -> Some c | _ -> None))
        Assert.Equal("1", string call.Arguments["x"])
        let tool = Microsoft.Extensions.AI.ChatMessage(ChatRole.Tool, [| FunctionResultContent(call.CallId, "answer") :> AIContent |])
        let! _ = client.GetResponseAsync([| user; assistant; tool |], null, CancellationToken.None)
        Assert.Equal(0, transport.Posted)
        Assert.Equal(2, transport.Streamed)
        Assert.Equal("https://chatgpt.com/backend-api/codex/responses", transport.Endpoint)
        use body = JsonDocument.Parse(transport.Body)
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean())
        Assert.False(body.RootElement.GetProperty("store").GetBoolean())
        Assert.Contains("function_call_output", transport.Body)
        Assert.Contains("protected-codex", transport.Body)
    }

    [<Theory>]
    [<InlineData("grok")>]
    [<InlineData("openrouter")>]
    [<InlineData("kimi-code-plan-cn")>]
    [<InlineData("github-copilot")>]
    [<InlineData("kilo")>]
    [<InlineData("cline-pass")>]
    member _.``subscription factory remains offline and MEAI preserves streamed tools and reasoning``(provider: string) = task {
        let transport = SubscriptionTransport(0, false)
        let factory, credentials = SubscriptionIntegration.factory provider transport
        let model = factory.CreateClient(provider, "account", "test-model")
        Assert.Equal(0, credentials.Calls)
        use client = model.AsIChatClient()
        let user = Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "lookup")
        let! response = client.GetStreamingResponseAsync([| user |], null, CancellationToken.None).ToChatResponseAsync(CancellationToken.None)
        let assistant = Assert.Single(response.Messages)
        Assert.Equal("checking", assistant.Text)
        let call = Assert.Single(assistant.Contents |> Seq.choose (function :? FunctionCallContent as c -> Some c | _ -> None))
        Assert.Equal("1", string call.Arguments["x"])
        Assert.Contains("protected-thinking", assistant.AdditionalProperties["FSharp.ModelsDev.ProviderState"] :?> string)
        let tool = Microsoft.Extensions.AI.ChatMessage(ChatRole.Tool, [| FunctionResultContent("call", "answer") :> AIContent |])
        let! completed = client.GetResponseAsync([| user; assistant; tool |], null, CancellationToken.None)
        Assert.Equal("checking", completed.Text)
        use body = JsonDocument.Parse(transport.Body)
        if provider = "kimi-code-plan-cn" then
            Assert.Contains("protected-thinking", transport.Body)
            Assert.Contains("tool_result", transport.Body)
            Assert.Equal(SubscriptionIntegration.endpoints provider + "/messages", transport.Url)
            Assert.Equal("KimiCLI/1.49.0", transport.Headers["User-Agent"])
        else
            let assistantWire = body.RootElement.GetProperty("messages").[1]
            Assert.Equal("protected-thinking", assistantWire.GetProperty("reasoning_content").GetString())
            Assert.Equal(SubscriptionIntegration.endpoints provider + "/chat/completions", transport.Url)
        if provider = "github-copilot" then Assert.Equal("agent", transport.Headers["X-Initiator"])
        if provider = "kilo" then Assert.Equal("org", transport.Headers["X-KILOCODE-ORGANIZATIONID"])
    }

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``401 retries once before output for complete and stream``(stream: bool) = task {
        let transport = SubscriptionTransport(401, false)
        let factory, credentials = SubscriptionIntegration.factory "grok" transport
        let client = factory.CreateClient("grok", "account", "test-model")
        let request = ChatRequest("test-model", [| FSharp.ModelsDev.ChatMessage.User("hello") |])
        if stream then
            let iterator = client.Stream(request, CancellationToken.None).GetAsyncEnumerator()
            let mutable more = true
            while more do
                let! moved = iterator.MoveNextAsync().AsTask()
                more <- moved
            do! iterator.DisposeAsync().AsTask()
        else
            let! _ = client.Complete(request, CancellationToken.None)
            ()
        Assert.Equal(2, transport.Calls)
        Assert.Equal(1, credentials.Invalidations)
    }

    [<Fact>]
    member _.``403 never retries and stream failure after output never replays``() = task {
        let denied = SubscriptionTransport(403, false)
        let factory, credentials = SubscriptionIntegration.factory "grok" denied
        let client = factory.CreateClient("grok", "account", "test-model")
        let request = ChatRequest("test-model", [||])
        let! _ = Assert.ThrowsAsync<AiException>(Func<Task>(fun () -> client.Complete(request, CancellationToken.None) :> Task))
        Assert.Equal(1, denied.Calls)
        Assert.Equal(0, credentials.Invalidations)
        let late = SubscriptionTransport(0, true)
        let secondFactory, secondCredentials = SubscriptionIntegration.factory "grok" late
        let iterator = secondFactory.CreateClient("grok", "account", "test-model").Stream(request, CancellationToken.None).GetAsyncEnumerator()
        let consume = task {
            let mutable more = true
            while more do
                let! moved = iterator.MoveNextAsync().AsTask()
                more <- moved
        }
        let! _ = Assert.ThrowsAsync<AiException>(Func<Task>(fun () -> consume :> Task))
        do! iterator.DisposeAsync().AsTask()
        Assert.Equal(1, late.Calls)
        Assert.Equal(0, secondCredentials.Invalidations)
    }

    [<Fact>]
    member _.``discovered models are account bound copied and cannot leak tokens to another origin``() = task {
        let transport = SubscriptionTransport(0, false)
        let factory, _ = SubscriptionIntegration.factory "grok" transport
        let descriptor = ProviderModelDescriptor("grok", "new-model", "openai-chat", SubscriptionIntegration.endpoints "grok")
        descriptor.Authority <- "test-authority"
        descriptor.AccountId <- "other-account"
        Assert.Throws<AiException>(fun () -> factory.CreateClient("account", descriptor) |> ignore) |> ignore
        descriptor.AccountId <- "account"
        let client = factory.CreateClient("account", descriptor)
        descriptor.Authority <- "changed-after-construction"
        let! _ = client.Complete(ChatRequest("new-model", [||]), CancellationToken.None)
        let options = ProviderOptions(BaseUrl = "https://untrusted.test/v1")
        let unsafeClient = factory.CreateClient("grok", "account", "test-model", options)
        let! _ = Assert.ThrowsAsync<AiException>(Func<Task>(fun () -> unsafeClient.Complete(ChatRequest("test-model", [||]), CancellationToken.None) :> Task))
        Assert.Equal(1, transport.Calls)
    }
