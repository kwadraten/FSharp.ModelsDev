# FSharp.ModelsDev

English | [简体中文](https://github.com/kwadraten/FSharp.ModelsDev/blob/main/README.zh-CN.md)

FSharp.ModelsDev is a .NET 8 LLM SDK written in F#. It combines native provider protocols, a compiled-in [models.dev](https://models.dev/) catalog, and a Microsoft.Extensions.AI adapter. The public API is usable from F# and C#.

The catalog works offline. Inference and authentication use HTTP directly; the library does not launch official CLIs or read their credential files. Applications supply credentials, browser interaction, and persistent token storage.

## Install

```sh
dotnet add package FSharp.ModelsDev
```

The package targets `net8.0`. Its HTTP and JSON implementation uses .NET libraries; `Microsoft.Extensions.AI.Abstractions` provides the chat-client integration.

## Chat

Set `OPENAI_API_KEY` in the application environment:

```fsharp
open System
open System.Threading
open FSharp.ModelsDev

let client = ChatClient.OpenAI(Environment.GetEnvironmentVariable("OPENAI_API_KEY"), "gpt-5")
let request = ChatRequest("gpt-5", [| ChatMessage.User "Explain discriminated unions in one sentence." |])
let response = client.Complete(request, CancellationToken.None).GetAwaiter().GetResult()
printfn "%s" response.Message.Text
```

The same API from C#:

```csharp
using System;
using System.Threading;
using FSharp.ModelsDev;

var client = ChatClient.OpenAI(Environment.GetEnvironmentVariable("OPENAI_API_KEY"), "gpt-5");
var request = new ChatRequest("gpt-5", new[] { ChatMessage.User("Explain discriminated unions in one sentence.") });
var response = await client.Complete(request, CancellationToken.None);
Console.WriteLine(response.Message.Text);
```

`Complete` returns the assistant message, response ID, model, finish reason, and token usage. Optional request properties include `MaxTokens`, `Temperature`, `ReasoningEffort`, and `ReasoningBudgetTokens`; support depends on the selected backend and model.

## Streaming and tools

`Stream` yields text deltas, tool argument deltas, usage, provider state, and a terminal event:

```csharp
await foreach (var update in client.Stream(request, CancellationToken.None))
{
    if (update is TextDelta text) Console.Write(text.Text);
    if (update is Completed done) Console.WriteLine($"\n{done.FinishReason}");
}
```

Declare tools with `ToolDefinition(name, description, parametersJson)` and assign them to `ChatRequest.Tools`. Append the returned assistant message and `ChatMessage.ToolResult(call.Id, result)` to the next request, carrying the tool definitions forward. Preserve the complete assistant message, including `ProviderStateJson`: reasoning signatures and protected state may be required on the next turn.

## Microsoft.Extensions.AI

`AsIChatClient()` exposes the same model through `IChatClient`, including streaming, function-call contents, usage, and multi-turn provider state:

```fsharp
open FSharp.ModelsDev.MEAi

let ask () = task {
    use ai = client.AsIChatClient()
    let messages = [| Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Hello") |]
    let! response = ai.GetResponseAsync(messages, null, CancellationToken.None)
    printfn "%s" response.Text
}
ask().GetAwaiter().GetResult()
```

## Offline model selection

`SnapshotCatalog` queries an embedded snapshot without HTTP, credentials, or a separate data download. Model offerings are identified by provider and model ID; different providers may expose different limits for the same underlying model.

```fsharp
open FSharp.ModelsDev.Catalog

let catalog = SnapshotCatalog()
let filter =
    ModelFilter(
        RequiredInputModalities = [| "image" |],
        RequiredCapabilities = ModelCapabilities.ToolCalling,
        MinContextTokens = Nullable 131072L,
        MinOutputTokens = Nullable 8192L)

for model in ModelCatalogQuery.FindModels(catalog, filter) do
    printfn "%s/%s" model.ProviderId model.ModelId
```

`ModelInfo` exposes context, input and output token limits, input/output modalities, tool/reasoning/structured-output capabilities, attachment and temperature support, family, type, status, dates, and open-weight status. Missing numeric and Boolean metadata stays nullable; dates retain source precision. Input limits are not inferred from context minus output limits. Pricing is not currently exposed as a typed API.

`FindModels` searches a catalog; `FilterModels` applies the same conditions to an existing collection. Conditions combine with AND, identifier lists with OR, and required modalities/capabilities must all match. Strings use ordinal comparison. Unknown numeric/Boolean values do not satisfy a specified condition. Results sort by provider ID and model ID.

Catalog capabilities describe the source data; they do not establish SDK transport support or account entitlement. Account-specific discovery is a separate, explicit operation. `SnapshotVersion` identifies the embedded data.

## Providers

Use `ProviderFactory` to select a backend and provide account-scoped credentials:

```fsharp
open FSharp.ModelsDev.Providers

let factory = ProviderFactory(catalog, ApiKeyCredentialProvider(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
let responses = factory.CreateClient("openai", "personal", "gpt-5", ProviderOptions(ProtocolId = "openai-responses"))
```

| Provider ID | Protocol | Credentials |
| --- | --- | --- |
| `openai` and compatible catalog providers | `openai-chat` | Bearer API key |
| `openai` with an explicit protocol | `openai-responses` | Bearer API key |
| `openai-codex` | `codex-responses` | Host-supplied ChatGPT access token and account header |
| `google` | `gemini` | `ApiKeyCredentialProvider.Gemini(key)` |
| `anthropic` | `anthropic-messages` | `ApiKeyCredentialProvider.Anthropic(key)` |
| `google-vertex` | `vertex-gemini` | Host-supplied Google Bearer token, project and location |
| `google-vertex-anthropic` | `vertex-anthropic` | Host-supplied Google Bearer token, project and location |
| `antigravity` | `antigravity` | Google OAuth session and Cloud Code project |

`ProviderOptions` can override the protocol, base URL, project, location, headers, credentials, discovered model, and session ID. Use `BearerTokenCredentialProvider` for asynchronous host token retrieval. Client creation does not fetch credentials or discover account models.

Codex subscription endpoints require streaming HTTP. Their `Complete` implementation aggregates the terminal stream response, preserving tools, IDs, usage, and reasoning state. Codex sign-in and token renewal remain the host's responsibility. Ordinary Responses API completions retain non-streaming HTTP.

Antigravity uses an unofficial Cloud Code interface. Supply `GoogleOAuthOptions.ClientId` and, if required by the OAuth client, `ClientSecret`; neither is embedded. The host can implement `IOAuthTokenStore` for persistence. Vertex project/location are configuration, not credentials.

## Subscription authentication

`ProviderAuthClient` implements independent HTTP login, refresh where supported, and authenticated model discovery:

| Options preset | Provider ID | Login |
| --- | --- | --- |
| `Grok()` | `grok` | Browser PKCE or device code; distinct from the `xai` API-key service |
| `OpenRouter()` | `openrouter` | Browser PKCE or manual authorization code, yielding an API key |
| `KimiCode(false)` / `KimiCode(true)` | `kimi-code-plan-cn` / `kimi-code-plan-global` | Regional device-code OAuth |
| `GitHubCopilot()` | `github-copilot` | GitHub device OAuth and Copilot token exchange |
| `Kilo()` | `kilo` | Device authorization |
| `ClinePass()` | `cline-pass` | WorkOS device authorization and Cline registration |

For example, a host can display the Kimi verification URI and user code, wait for approval, then select an authorized model:

```fsharp
let login () = task {
    use auth = new ProviderAuthClient(ProviderAuthOptions.KimiCode(false), InMemoryProviderAuthSessionStore())
    let ct = CancellationToken.None
    let! device = auth.BeginDeviceLoginAsync(ct)
    printfn "Open %O and enter %s" device.VerificationUri device.UserCode
    let! _ = auth.CompleteDeviceLoginAsync(device, "personal", ct)
    let! models = auth.DiscoverModelsAsync("personal", ct)
    if models.Count = 0 then invalidOp "No authorized models were returned."
    let selected = models[0] // Replace with the application's model selection.
    let factory = ProviderFactory(catalog, auth.CreateCredentialProvider())
    let model = factory.CreateClient("personal", selected)
    let! reply = model.Complete(ChatRequest(selected.ModelId, [| ChatMessage.User "Hello" |]), ct)
    printfn "%s" reply.Message.Text
}
```

Sessions default to memory. Implement `IProviderAuthSessionStore` to persist complete, rotated sessions by provider, authority, and account; keep `ProviderAuthClient` alive while using its credential provider. Kimi Code defaults to Messages for structured tools and thinking state. Browser flows expose `BeginBrowserLoginAsync` and `CompleteBrowserLoginAsync` so the host controls interaction.

## Verification

CI builds and tests on Linux and Windows, checks the catalog checksum and generated indexes, and installs the packed NuGet artifact into standalone F# and C# consumers. HTTP/SSE recordings cover provider request shapes, streaming and tool-result replay without contacting upstream services; real API tests require explicit opt-in.

Development rules, test commands, catalog maintenance, and tag-based trusted publishing are documented in [AGENTS.md](https://github.com/kwadraten/FSharp.ModelsDev/blob/main/AGENTS.md). Recording provenance and licensing are documented in the [fixture notice](https://github.com/kwadraten/FSharp.ModelsDev/blob/main/tests/FSharp.ModelsDev.Tests/Fixtures/Recordings/NOTICE.md).

## License

[MIT](https://github.com/kwadraten/FSharp.ModelsDev/blob/main/LICENSE). Model metadata is sourced from [models.dev](https://models.dev/); imported test recordings retain their upstream notices.
