# FSharp.ModelsDev

[English](https://github.com/kwadraten/FSharp.ModelsDev/blob/main/README.md) | 简体中文

FSharp.ModelsDev 是使用 F# 编写的 .NET 8 LLM SDK，集成原生提供者协议、内嵌的 [models.dev](https://models.dev/) 模型目录，以及 Microsoft.Extensions.AI 适配器。公共 API 可直接用于 F# 和 C#。

模型目录支持离线查询。推理与认证直接使用 HTTP；库不会启动官方 CLI，也不会读取其凭据文件。凭据、浏览器交互和令牌持久化由宿主提供。

## 安装

```sh
dotnet add package FSharp.ModelsDev
```

包以 `net8.0` 为目标框架。HTTP 和 JSON 使用 .NET 自带实现，聊天客户端集成使用 `Microsoft.Extensions.AI.Abstractions`。

## 聊天

在应用环境中设置 `OPENAI_API_KEY`：

```fsharp
open System
open System.Threading
open FSharp.ModelsDev

let client = ChatClient.OpenAI(Environment.GetEnvironmentVariable("OPENAI_API_KEY"), "gpt-5")
let request = ChatRequest("gpt-5", [| ChatMessage.User "用一句话解释可辨识联合。" |])
let response = client.Complete(request, CancellationToken.None).GetAwaiter().GetResult()
printfn "%s" response.Message.Text
```

相同的 C# API：

```csharp
using System;
using System.Threading;
using FSharp.ModelsDev;

var client = ChatClient.OpenAI(Environment.GetEnvironmentVariable("OPENAI_API_KEY"), "gpt-5");
var request = new ChatRequest("gpt-5", new[] { ChatMessage.User("用一句话解释可辨识联合。") });
var response = await client.Complete(request, CancellationToken.None);
Console.WriteLine(response.Message.Text);
```

`Complete` 返回助手消息、响应 ID、模型、结束原因与 token 用量。请求可选参数包括 `MaxTokens`、`Temperature`、`ReasoningEffort` 和 `ReasoningBudgetTokens`，具体支持情况取决于后端与模型。

## 流式输出与工具

`Stream` 输出文本增量、工具参数增量、用量、提供者状态和终止事件：

```csharp
await foreach (var update in client.Stream(request, CancellationToken.None))
{
    if (update is TextDelta text) Console.Write(text.Text);
    if (update is Completed done) Console.WriteLine($"\n{done.FinishReason}");
}
```

使用 `ToolDefinition(name, description, parametersJson)` 声明工具并设置 `ChatRequest.Tools`。下一轮请求应依次加入返回的助手消息和 `ChatMessage.ToolResult(call.Id, result)`，并继续传入工具定义。保留完整助手消息及其 `ProviderStateJson`，后续调用可能需要其中的推理签名和受保护状态。

## Microsoft.Extensions.AI

`AsIChatClient()` 将相同模型暴露为 `IChatClient`，支持流式、函数调用内容、用量和多轮提供者状态：

```fsharp
open FSharp.ModelsDev.MEAi

let ask () = task {
    use ai = client.AsIChatClient()
    let messages = [| Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "你好") |]
    let! response = ai.GetResponseAsync(messages, null, CancellationToken.None)
    printfn "%s" response.Text
}
ask().GetAwaiter().GetResult()
```

## 离线选择模型

`SnapshotCatalog` 直接查询内嵌快照，无需 HTTP、凭据或单独下载数据。模型条目按提供者和模型 ID 标识；相同基础模型在不同提供者处可能具有不同限制。

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

`ModelInfo` 提供上下文、输入和输出 token 上限，双向模态，工具、推理、结构化输出能力，附件与温度支持，系列、类型、状态、日期和开放权重标志。缺失数值和布尔信息保持 nullable，日期保留源数据精度。库不会用上下文减去输出长度推算输入上限。目前没有公开类型化价格 API。

`FindModels` 查询目录，`FilterModels` 对已有集合使用相同条件。不同条件取 AND，同一标识字段的候选值取 OR，所需模态和能力必须全部满足。字符串按 ordinal 比较，未知数值或布尔值不满足已设置的条件。结果按提供者 ID、模型 ID 排序。

目录能力描述源数据，不代表 SDK 传输支持或账户权限。账户模型发现是独立、显式的操作。`SnapshotVersion` 标识当前内嵌数据。

## 提供者

使用 `ProviderFactory` 选择后端并传入账户凭据：

```fsharp
open FSharp.ModelsDev.Providers

let factory = ProviderFactory(catalog, ApiKeyCredentialProvider(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
let responses = factory.CreateClient("openai", "personal", "gpt-5", ProviderOptions(ProtocolId = "openai-responses"))
```

| 提供者 ID | 协议 | 凭据 |
| --- | --- | --- |
| `openai` 及兼容目录提供者 | `openai-chat` | Bearer API key |
| 显式选择协议的 `openai` | `openai-responses` | Bearer API key |
| `openai-codex` | `codex-responses` | 宿主提供的 ChatGPT access token 与账户 header |
| `google` | `gemini` | `ApiKeyCredentialProvider.Gemini(key)` |
| `anthropic` | `anthropic-messages` | `ApiKeyCredentialProvider.Anthropic(key)` |
| `google-vertex` | `vertex-gemini` | 宿主提供的 Google Bearer token、项目与区域 |
| `google-vertex-anthropic` | `vertex-anthropic` | 宿主提供的 Google Bearer token、项目与区域 |
| `antigravity` | `antigravity` | Google OAuth 会话与 Cloud Code 项目 |

`ProviderOptions` 可覆盖协议、基础 URL、项目、区域、headers、凭据、发现的模型和会话 ID。异步取得宿主令牌时使用 `BearerTokenCredentialProvider`。创建客户端不会获取凭据或发现账户模型。

Codex 订阅接口要求 HTTP 流式调用，其 `Complete` 会聚合终止响应，保留工具、ID、用量和推理状态。Codex 登录与令牌续期仍由宿主负责。普通 Responses API 的完整响应继续使用非流式 HTTP。

Antigravity 使用非官方 Cloud Code 接口。宿主须提供 `GoogleOAuthOptions.ClientId`，并按 OAuth 客户端要求提供 `ClientSecret`；库不内置这两个值。宿主可实现 `IOAuthTokenStore` 持久化会话。Vertex 的项目与区域属于配置，不能替代凭据。

## 订阅认证

`ProviderAuthClient` 独立通过 HTTP 完成登录、受支持的令牌刷新和账户模型发现：

| 配置预设 | 提供者 ID | 登录方式 |
| --- | --- | --- |
| `Grok()` | `grok` | 浏览器 PKCE 或设备码；与 `xai` API-key 服务分开 |
| `OpenRouter()` | `openrouter` | 浏览器 PKCE 或手动授权码，最终获取 API key |
| `KimiCode(false)` / `KimiCode(true)` | `kimi-code-plan-cn` / `kimi-code-plan-global` | 国内 / 国际设备码 OAuth |
| `GitHubCopilot()` | `github-copilot` | GitHub 设备 OAuth，再交换 Copilot 令牌 |
| `Kilo()` | `kilo` | 设备授权 |
| `ClinePass()` | `cline-pass` | WorkOS 设备授权及 Cline 注册 |

例如，宿主显示 Kimi 验证地址与用户码，等待用户授权后选择账户模型：

```fsharp
let login () = task {
    use auth = new ProviderAuthClient(ProviderAuthOptions.KimiCode(false), InMemoryProviderAuthSessionStore())
    let ct = CancellationToken.None
    let! device = auth.BeginDeviceLoginAsync(ct)
    printfn "打开 %O，输入 %s" device.VerificationUri device.UserCode
    let! _ = auth.CompleteDeviceLoginAsync(device, "personal", ct)
    let! models = auth.DiscoverModelsAsync("personal", ct)
    if models.Count = 0 then invalidOp "账户未返回可用模型。"
    let selected = models[0] // 替换为应用自己的模型选择逻辑。
    let factory = ProviderFactory(catalog, auth.CreateCredentialProvider())
    let model = factory.CreateClient("personal", selected)
    let! reply = model.Complete(ChatRequest(selected.ModelId, [| ChatMessage.User "你好" |]), ct)
    printfn "%s" reply.Message.Text
}
```

默认会话保存在内存。持久化时实现 `IProviderAuthSessionStore`，按提供者、认证域和账户保存完整的轮换后会话；使用凭据提供器期间保持 `ProviderAuthClient` 存活。Kimi Code 默认使用 Messages 协议保留结构化工具和 thinking 状态。浏览器流程提供 `BeginBrowserLoginAsync`、`CompleteBrowserLoginAsync`，交互由宿主控制。

## 验证

CI 在 Linux 和 Windows 构建并运行测试，检查目录校验和与生成索引，再将打包产物安装到独立 F#、C# 项目中验证。HTTP/SSE 录制样本用于离线验证请求格式、流式响应与工具结果回传；真实接口测试需要显式启用。

开发约束、测试命令、目录维护和基于 tag 的信任发布见 [AGENTS.md](https://github.com/kwadraten/FSharp.ModelsDev/blob/main/AGENTS.md)。录制样本的来源及许可证见[样本说明](https://github.com/kwadraten/FSharp.ModelsDev/blob/main/tests/FSharp.ModelsDev.Tests/Fixtures/Recordings/NOTICE.md)。

## 许可证

[MIT](https://github.com/kwadraten/FSharp.ModelsDev/blob/main/LICENSE)。模型元数据来自 [models.dev](https://models.dev/)，导入的测试录制保留上游声明。
