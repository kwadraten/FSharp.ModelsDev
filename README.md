# FSharp.ModelsDev

F# LLM API SDK（net8.0）：支持 OpenAI、Google Gemini、Vertex AI、Anthropic 和 Antigravity；既可独立使用 `ChatClient`，也可经 `AsIChatClient()` 接入 Microsoft.Extensions.AI 的 `IChatClient`。内置 models.dev 离线模型目录；除 `Microsoft.Extensions.AI.Abstractions` 外无额外第三方运行时依赖。

## 仓库布局

```text
src/FSharp.ModelsDev/                      SDK 源码
tests/FSharp.ModelsDev.Tests/              测试
tools/FSharp.ModelsDev.Catalog.Generator/  模型目录生成器
catalog/                                  内嵌 models.dev 快照
```

## 用法

### 1. 独立使用 ChatClient

```fsharp
open System
open FSharp.ModelsDev

let client = ChatClient.OpenAI(apiKey, "gpt-5")

let request = ChatRequest("gpt-5", [| ChatMessage.User "你好" |])
request.Temperature <- Nullable 0.7
request.MaxTokens <- Nullable 1024
request.ReasoningEffort <- "medium"

task {
    // 一次性补全
    let! response = client.Complete(request)
    printfn "%s" response.Message.Text

    // 流式输出：迭代 ChatStreamEvent
    request.Stream <- true
    for ev in client.Stream(request) do
        match ev with
        | :? TextDelta as d -> printf "%s" d.Text
        | :? Completed as c -> printfn "\n[finish=%A]" c.FinishReason
        | _ -> ()
}
|> ignore
```

`ChatClient.OpenAI(apiKey, model, ?baseUrl, ?transport)` 是便捷封装；主构造器为
`ChatClient(model, protocol: IChatProtocol, transport: IChatTransport, url, headers)`，可完全手动组装（自带 transport 时通常用于测试）。

### 2. 目录查询

```fsharp
open FSharp.ModelsDev.Catalog

let catalog = SnapshotCatalog() :> IModelCatalog

printfn "快照版本: %s" catalog.SnapshotVersion
printfn "提供者: %A" catalog.ProviderIds

match catalog.TryGet("openai", "gpt-5") with
| true, model ->
    printfn "%s/%s: tools=%b reasoning=%b"
        model.ProviderId model.ModelId model.SupportsTools model.SupportsReasoning
| false, _ ->
    printfn "未找到该模型"

let models = catalog.GetModels("openai")
```

`ModelInfo` 还提供 models.dev 快照中的可选元数据：`MaxInputTokens`、`InputModalities`、`OutputModalities`、`Description`、`Family`、`ModelType`、`Status`、`KnowledgeCutoff`、`ReleaseDate`、`LastUpdated`、`SupportsAttachments`、`SupportsTemperature` 和 `OpenWeights`。数字与布尔值使用可空类型；`null` / 空集合表示快照没有提供该值。日期和知识截止时间保留为原始字符串，因此可能是部分日期。`ModelInfo` 通过 `ModelMetadata` 构造时会复制元数据及 modality 列表；原有不带 `ModelMetadata` 的构造方式仍可用，未提供的新字段保持未知。

可用 `ModelCatalogQuery.FindModels(catalog, filter)` 在离线目录中查找，或对已有模型集合调用 `ModelCatalogQuery.FilterModels(models, filter)`。筛选器中所有已设置的条件都必须满足；同一标识字段的列表（provider、family、model type、status）内任一值匹配即可，模态条件要求模型包含所列的全部模态，能力条件要求包含 `RequiredCapabilities` 中的全部标志。字符串按序号比较。未提供的数值或布尔元数据无法满足对应条件；空列表、`null` 列表及未设置的 nullable 条件不增加限制。数值下限允许设置为零，负数无效；上下文、输入和输出 token 分别筛选，不会从上下文长度减去输出长度来推断输入上限。结果按 provider ID、再按 model ID 稳定排序。

以下例子筛选支持图像输入、工具调用、上下文至少 131072 tokens、最大输出至少 8192 tokens 的模型：

```fsharp
open System
open FSharp.ModelsDev.Catalog

let filter = ModelFilter()
filter.RequiredInputModalities <- [| "image" |]
filter.RequiredCapabilities <- ModelCapabilities.ToolCalling
filter.MinContextTokens <- Nullable 131072L
filter.MinOutputTokens <- Nullable 8192L

let matches = ModelCatalogQuery.FindModels(catalog, filter)
```

```csharp
using System;
using FSharp.ModelsDev.Catalog;

var catalog = new SnapshotCatalog();
var filter = new ModelFilter
{
    RequiredInputModalities = new[] { "image" },
    RequiredCapabilities = ModelCapabilities.ToolCalling,
    MinContextTokens = 131072L,
    MinOutputTokens = 8192L
};

var matches = ModelCatalogQuery.FindModels(catalog, filter);
```

这些字段描述目录快照中的模型元数据，不保证当前 SDK 后端或具体传输支持相应模态，也不代表当前账户有权调用该模型。账户在线发现结果保持独立，不会自动合并到离线目录或补充其元数据。

### 3. 接入 Microsoft.Extensions.AI

```fsharp
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog
open FSharp.ModelsDev.MEAi

let catalog = SnapshotCatalog() :> IModelCatalog
let credentials = ApiKeyCredentialProvider(apiKey)
let factory = ProviderFactory(catalog, credentials)

let meaiClient: Microsoft.Extensions.AI.IChatClient =
    factory.CreateClient("openai", "default", "gpt-5").AsIChatClient()

let messages =
    ResizeArray<Microsoft.Extensions.AI.ChatMessage>
        [ Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "你好") ]

task {
    let! response = meaiClient.GetResponseAsync(messages)
    printfn "%s" response.Text
}
|> ignore
```

## 后端与认证

所有后端提供非流式/流式聊天、工具调用和结果回传、图像输入，以及后端支持的推理控制。图像接受 data URI 或该接口原生支持的 URI；不支持的输入和参数会明确报错。

| providerId | protocolId | 认证 |
| --- | --- | --- |
| `openai` 及兼容提供者 | `openai-chat`（默认） | Bearer API Key |
| `openai`（显式选择） | `openai-responses` | Bearer API Key |
| `openai-codex` | `codex-responses` | 宿主提供的 ChatGPT OAuth Bearer token |
| `kimi-code-plan-cn` / `kimi-code-plan-global` | `kimi-code` | Kimi OAuth 或显式 API key |
| `google` | `gemini` | `x-goog-api-key` |
| `google-vertex` | `vertex-gemini` | 可刷新的 Google Bearer token + project/location |
| `anthropic` | `anthropic-messages` | `x-api-key` |
| `google-vertex-anthropic` | `vertex-anthropic` | 可刷新的 Google Bearer token + project/location |
| `antigravity` | `antigravity` | Google OAuth 会话 + Cloud Code 项目 |

`antigravity` 是运行时内置提供者，不修改 models.dev 快照。工厂创建客户端时不读取凭据或访问网络；每次发出请求时异步获取当前凭据。

### Gemini、Anthropic 与 Responses

```fsharp
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog
open FSharp.ModelsDev.Providers

let catalog = SnapshotCatalog() :> IModelCatalog
let google = ProviderFactory(catalog, ApiKeyCredentialProvider.Gemini(geminiApiKey))
let gemini = google.CreateClient("google", "default", geminiModelId)

let anthropic = ProviderFactory(catalog, ApiKeyCredentialProvider.Anthropic(anthropicApiKey))
let claude = anthropic.CreateClient("anthropic", "default", claudeModelId)

let openai = ProviderFactory(catalog, ApiKeyCredentialProvider(openaiApiKey))
let responses =
    openai.CreateClient(
        "openai", "default", openaiModelId,
        ProviderOptions(ProtocolId = "openai-responses"))
```

`EnvCredentialProvider` 根据提供者选择正确的 API Key header；Vertex 的项目、区域、凭据文件环境变量只作为配置，不能代替 access token。
其他 OpenAI 兼容提供者优先使用目录中的 API URL；目录未提供 URL 时，需要显式设置 `ProviderOptions.BaseUrl`。

### Vertex AI

宿主通过异步回调获取当前 access token，可在宿主内使用 ADC、服务账户或自己的刷新机制。SDK 不执行 `gcloud` 子进程。

```fsharp
open System
open System.Threading
open System.Threading.Tasks

// getCurrentGoogleToken: string -> CancellationToken -> Task<string>
let credentials =
    BearerTokenCredentialProvider(
        Func<string, CancellationToken, Task<string>>(fun account ct ->
            getCurrentGoogleToken account ct))

let factory = ProviderFactory(catalog, credentials)
let options = ProviderOptions(ProjectId = projectId, Location = location)
let vertex = factory.CreateClient("google-vertex", "default", geminiModelId, options)
let vertexClaude = factory.CreateClient("google-vertex-anthropic", "default", vertexClaudeModelId, options)
```

未显式设置 project/location 时读取 `GOOGLE_VERTEX_PROJECT` / `GOOGLE_VERTEX_LOCATION`；缺失时提示配置。`global` 使用全局主机，模型 ID 应为相应 Vertex 提供者接受的 ID。

### Antigravity OAuth

Antigravity 是非官方兼容适配，使用 Cloud Code 内部接口。OAuth 客户端、scope、redirect URI 与 headers 的参考来源固定为 [opencode-antigravity-auth 的提交 16e0056](https://github.com/NoeFabris/opencode-antigravity-auth/tree/16e0056431d0a1291ee66e5938c732720b13a851)，可通过 `GoogleOAuthOptions` 和 `ProviderOptions` 覆盖。库不内置 OAuth Client ID 或 Client Secret，宿主必须显式提供与目标服务兼容的 OAuth 客户端配置。默认生成端点为 `https://cloudcode-pa.googleapis.com`。

```fsharp
open System.Diagnostics

let store = InMemoryOAuthTokenStore() :> IOAuthTokenStore
let oauthOptions = GoogleOAuthOptions()
oauthOptions.ClientId <- Environment.GetEnvironmentVariable("GOOGLE_OAUTH_CLIENT_ID")
// OAuth 客户端要求 secret 时由宿主提供；public client 可留空。
oauthOptions.ClientSecret <- Environment.GetEnvironmentVariable("GOOGLE_OAUTH_CLIENT_SECRET")
use credentials = new GoogleOAuthCredentialProvider(oauthOptions, store)

let openBrowser =
    Func<Uri, CancellationToken, Task>(fun uri _ ->
        Process.Start(ProcessStartInfo(uri.AbsoluteUri, UseShellExecute = true)) |> ignore
        Task.CompletedTask)

task {
    let! _ = credentials.Client.LoginAsync(openBrowser, "personal", CancellationToken.None)
    let factory = ProviderFactory(catalog, credentials)
    let client = factory.CreateClient("antigravity", "personal", antigravityModelId)
    let! response =
        client.Complete(
            ChatRequest(antigravityModelId, [| ChatMessage.User "你好" |]),
            CancellationToken.None)
    printfn "%s" response.Message.Text
}
|> ignore
```

默认回调是 `http://localhost:51121/oauth-callback`，登录窗口有效期五分钟。也可使用 `BeginLogin()` 获取授权 URL，再将完整回调 URL 交给 `CompleteLoginAsync()`，由宿主控制交互。

默认令牌只保存在内存；宿主可实现 `IOAuthTokenStore.LoadAsync/SaveAsync/DeleteAsync` 接入自己的持久化存储。存储按 accountId 隔离，保存 access token、refresh token、到期时间及项目。SDK 提前 60 秒刷新，并合并同一账户的并发刷新；工厂客户端在输出任何流事件之前遇到 401 最多刷新重试一次。

项目优先使用 `ProviderOptions.ProjectId`，其次使用会话项目，再通过 `loadCodeAssist` 发现；无法发现时提示显式配置。`ProviderOptions.BaseUrl` 可覆盖后端根地址，`Headers` 可覆盖工厂请求的协议 headers。

### 多轮工具调用与 Microsoft.Extensions.AI

将返回的完整 assistant 消息加入历史，再加入 `ChatMessage.ToolResult(call.Id, result)`。`ProviderStateJson` 保存 thought signature、thinking 块或加密 reasoning item，应随消息保留；只取出 `.Text` 重建消息会丢失这些状态。

`AsIChatClient()` 经 `AdditionalProperties["FSharp.ModelsDev.ProviderState"]` 保留状态，支持流聚合后继续工具调用；流中的 JSON 参数会按工具调用组装为完整 `FunctionCallContent`，最终用量只累计一次。一个 MEAI 工具消息包含多个结果时会逐个转换。

`ChatRequest.ReasoningEffort` 映射原生 effort/level，`ReasoningBudgetTokens` 用于接受显式预算的模型；二者受模型能力限制。MEAI 调用可在 `ChatOptions.AdditionalProperties` 中设置 `FSharp.ModelsDev.ReasoningEffort`（string）、`FSharp.ModelsDev.ReasoningBudgetTokens`（int）。

### 独立订阅登录与账户模型发现

`ProviderAuthClient` 通过 HTTP 完成首次登录、令牌刷新和模型发现，无需安装官方 CLI，也不读取其凭据。支持以下预设：

| 预设 | 工厂 providerId | 登录方式 |
| --- | --- | --- |
| `ProviderAuthOptions.Grok()` | `grok` | 浏览器 PKCE 或设备码；订阅推理网关与 `xai` API key 接口分开 |
| `ProviderAuthOptions.OpenRouter()` | `openrouter` | 浏览器 PKCE 或手动授权码，最终获取 API key |
| `ProviderAuthOptions.KimiCode(false / true)` | `kimi-code-plan-cn` / `kimi-code-plan-global` | 国内 / 国际设备码 OAuth |
| `ProviderAuthOptions.GitHubCopilot()` | `github-copilot` | GitHub 设备码，再交换短期 Copilot 推理令牌 |
| `ProviderAuthOptions.Kilo()` | `kilo` | Kilo 设备授权；可设置 `OrganizationId` |
| `ProviderAuthOptions.ClinePass()` | `cline-pass` | WorkOS 设备授权、Cline 注册及刷新 |

设备登录示例，浏览器交互由宿主决定：

```fsharp
open System.Threading
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog
open FSharp.ModelsDev.Providers

let store = InMemoryProviderAuthSessionStore()
use auth = new ProviderAuthClient(ProviderAuthOptions.KimiCode(false), store)

task {
    let ct = CancellationToken.None
    let! device = auth.BeginDeviceLoginAsync(ct)
    printfn "打开 %O，输入 %s" device.VerificationUri device.UserCode
    let! _ = auth.CompleteDeviceLoginAsync(device, "personal", ct)

    // 只有显式调用此方法才查询账户模型，不修改内嵌目录。
    let! models = auth.DiscoverModelsAsync("personal", ct)
    let selected = models[0] // 宿主应让用户选择，并处理空列表。
    let factory = ProviderFactory(SnapshotCatalog(), auth.CreateCredentialProvider())
    let model = factory.CreateClient("personal", selected)
    let! response = model.Complete(ChatRequest(selected.ModelId, [| ChatMessage.User "你好" |]), ct)
    printfn "%s" response.Message.Text
}
|> ignore
```

浏览器登录可调用 `BeginBrowserLoginAsync()` 获取地址，由宿主回传完整 callback URL 给 `CompleteBrowserLoginAsync(login, callbackUrl, accountId, ct)`。`LoginWithBrowserAsync(openBrowser, accountId, ct)` 则负责可选的本地回调监听；默认地址为 `http://localhost:51122/oauth-callback`。OpenRouter 无浏览器回调的宿主可先设置 `RedirectUri <- null`，再把页面显示的授权码交给完成方法。

`ProviderAuthOptions` 支持端点和公开 client ID 配置；自定义部署须同时配置 `TrustedOrigins`。Copilot 使用 `EnterpriseDomain` 指定企业域，也可配置 `CopilotTokenEndpoint` 等端点。账户权限由服务端决定；登录成功不保证拥有每个模型的调用权限。

默认会话仅保存在内存。宿主可实现 `IProviderAuthSessionStore` 持久化，键由 provider、认证域与账户组成；应保存完整会话，包括轮换后的刷新令牌及自有设备 ID。多个认证客户端共享同一存储实例时，进程内刷新会协调；跨进程持久化锁由宿主负责。使用凭据提供器期间请保持 `ProviderAuthClient` 存活。`LogoutAsync` 删除本地存储会话，不撤销服务端授权。

发现结果按账户和认证域绑定，工厂不会自动发现模型或静默切换账户。Kimi Code 默认通过 `kimi-code` 后端调用 Messages 接口，保留结构化工具调用和 thinking 状态；需要兼容旧 Chat Completions 时可显式设置 `ProviderOptions.ProtocolId <- "openai-chat"`。需要 Kimi thinking/effort 控制时传入发现结果；`ProviderOptions.SessionId` 可配置会话缓存键。原有 API key 方式保留。OpenRouter 和 Kilo 不假定存在刷新协议，凭据被撤销后需要重新授权。可刷新会话遇到 401 时最多重试一次；已输出流事件的请求不会重放，403 不触发认证重试。

### Codex 订阅的完整响应与流式调用

`openai-codex` 使用 `https://chatgpt.com/backend-api/codex` 和 `codex-responses` 协议。该接口要求 HTTP 请求启用流式；本库的 `Complete()` 会读取流并从终止响应返回完整 `ChatResponse`，保留响应 ID、模型、工具调用、reasoning 状态与用量，MEAI 的 `GetResponseAsync()` 同样适用。`Stream()` 继续逐事件输出。

```fsharp
// accessToken 与 chatgptAccountId 由宿主的认证集成提供；库不读取本机 CLI 文件。
let headers = System.Collections.Generic.Dictionary<string, string>()
headers["ChatGPT-Account-Id"] <- chatgptAccountId
let options = ProviderOptions(Headers = headers)
let factory = ProviderFactory(catalog, ApiKeyCredentialProvider(accessToken))
let client = factory.CreateClient("openai-codex", "personal", codexModelId, options)
```

已有 `openai` + `openai-responses` 配置若明确使用同一 ChatGPT Codex 地址，也会启用该聚合方式。普通 OpenAI Responses API 的 `Complete()` 保持非流式 POST。这里提供订阅请求适配；完整的 Codex 登录及令牌续期仍由宿主负责。

## 开发说明

测试包含从 OpenCode 移植的 HTTP/SSE 录制回放：离线检查实际请求、流式工具调用往返及响应解析。来源和适配说明见 [录制样本说明](tests/FSharp.ModelsDev.Tests/Fixtures/Recordings/NOTICE.md)，运行和显式录制方式见 [AGENTS.md](AGENTS.md#httpsse-录制回放)。

仓库设计约束、详细布局、开发验证和模型目录维护流程见 [AGENTS.md](AGENTS.md)。
