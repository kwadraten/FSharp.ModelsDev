# FSharp.ModelsDev

F# LLM API SDK（net8.0）：既可独立使用 `ChatClient`，也可经 `AsIChatClient()` 接入 Microsoft.Extensions.AI 的 `IChatClient`；内置 models.dev 离线模型目录。除 `Microsoft.Extensions.AI.Abstractions` 外无第三方依赖。

## 仓库布局

```
FSharp.ModelsDev/
├── src/FSharp.ModelsDev/                # 单 fsproj；文件编译顺序即内部分层（Contracts/Seams 约束先行）
│   ├── Contracts.fs                 #   ChatMessage / ChatRequest / ChatResponse / ChatStreamEvent
│   ├── Errors.fs                    #   AiError / AiException
│   ├── Seams.fs                     #   IChatTransport / IChatProtocol / IChatModel / ICredentialProvider
│   ├── CatalogTypes.fs              #   ModelInfo / ProviderDefinition / ModelCapabilities / ReasoningControl
│   ├── IModelCatalog.fs             #   IModelCatalog（纯离线目录 seam）
│   ├── Generated/*.g.fs             #   生成代码：ProviderIndex / ModelIndex
│   ├── SnapshotCatalog.fs           #   内嵌快照的 IModelCatalog 实现
│   ├── Sse.fs                       #   SSE 行切分
│   ├── OpenAICompatible.fs          #   "openai-chat" 协议实现
│   ├── CapabilityPolicy.fs          #   按 ModelCapabilities 校验/裁剪请求
│   ├── Credentials.fs               #   ApiKeyCredentialProvider 等
│   ├── ProviderRegistry.fs          #   providerId → protocol / credential / transport 组装
│   ├── ChatClient.fs                #   ChatClient：独立入口（IChatModel）
│   ├── ProviderFactory.fs           #   IChatClientFactory.CreateClient(providerId, accountId, modelId)
│   └── MEAi/                        #   AsIChatClient()：IChatModel → Microsoft.Extensions.AI.IChatClient
├── tests/FSharp.ModelsDev.Tests/        # 测试
├── tools/FSharp.ModelsDev.Catalog.Generator/  # 目录生成器：update-catalog / verify
└── catalog/                         # models.dev 快照（models.snapshot.json + .sha256）
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

## 目录维护

models.dev 快照（`catalog/models.snapshot.json` + `.sha256`）作为 EmbeddedResource 编译进程序集，普通构建与运行不联网。

```bash
# 联网更新快照，并重新生成 src/FSharp.ModelsDev/Generated/*.g.fs
dotnet run --project tools/FSharp.ModelsDev.Catalog.Generator -- update-catalog

# 校验快照与生成代码一致（CI 使用）
dotnet run --project tools/FSharp.ModelsDev.Catalog.Generator -- verify
```

## 设计约束

### Seam 边界

| Seam | 职责 | 不知道 |
| --- | --- | --- |
| `IModelCatalog` | 快照的纯离线查询（`SnapshotVersion` / `ProviderIds` / `TryGet` / `GetModels`） | 账户、凭据、HTTP |
| `CapabilityPolicy` | 按 `ModelCapabilities` 校验/裁剪请求参数 | 凭据、传输细节 |
| `IChatClientFactory`（`ProviderFactory`） | 按 `providerId + accountId + modelId` 组装 `IChatModel` | 线格式（归 Protocol）、快照内容（归 Catalog） |
| `ICredentialProvider` | `accountId` → 请求 headers | 模型能力、目录 |
| `IChatTransport` | POST 与 SSE 原始行 | 模型目录与能力 |
| `IChatProtocol` | 契约类型 ↔ wire JSON | 凭据、账户、HTTP 实现 |

### 公共 API 风格

对外 .NET 友好：公共签名只使用 class / interface / enum / `Task` / `IAsyncEnumerable` / `IReadOnlyList` / 可空引用（null 表示缺省），不出现 F# DU / option / Result / list / currying；内部实现自由使用 F# 函数式风格。

### 协议扩展点

`ProtocolDefinition.ProtocolId` 是开放字符串（内置 `"openai-chat"`）。接入新协议只需实现 `IChatProtocol`（`SerializeRequest` / `ParseResponse` / `TryParseStreamLine`），注册到 registry 即可；provider → protocol 的绑定来自目录快照。
