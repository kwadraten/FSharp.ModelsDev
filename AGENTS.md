# 仓库开发指南

本文件适用于整个仓库，供修改代码、测试和维护目录时使用。SDK 的配置与使用示例见 [README.md](README.md)。

<!-- CODEGRAPH_START -->
## CodeGraph

In repositories indexed by CodeGraph (a `.codegraph/` directory exists at the repo root), reach for it BEFORE grep/find or reading files when you need to understand or locate code:

- **MCP tool** (when available): `codegraph_explore` answers most code questions in one call — the relevant symbols' verbatim source plus the call paths between them, including dynamic-dispatch hops grep can't follow. Name a file or symbol in the query to read its current line-numbered source. If it's listed but deferred, load it by name via tool search.
- **Shell** (always works): `codegraph explore "<symbol names or question>"` prints the same output.

If there is no `.codegraph/` directory, skip CodeGraph entirely — indexing is the user's decision.
<!-- CODEGRAPH_END -->

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
│   ├── BackendCore.fs              #   多事件 SSE 帧解析
│   ├── Sse.fs                       #   SSE 行切分
│   ├── OpenAICompatible.fs          #   "openai-chat" 协议实现
│   ├── Gemini.fs / Anthropic.fs     #   原生协议及对应 Vertex 后端
│   ├── Antigravity.fs              #   Cloud Code 请求封装与项目发现
│   ├── GoogleOAuth.fs              #   PKCE 登录、账户存储接口及自动刷新
│   ├── OpenAIResponses.fs          #   Responses 协议与无服务端存储的历史回传
│   ├── OpenAIChatBackend.fs        #   Chat Completions 后端与请求时凭据
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

新增或移动 F# 文件时同步维护 `.fsproj` 的 `Compile` 顺序；依赖方向以工程文件为准，公共契约先于后端实现，MEAI 转换位于客户端之后。

## 设计约束

### Seam 边界

| Seam | 职责 | 不知道 |
| --- | --- | --- |
| `IModelCatalog` | 快照的纯离线查询（`SnapshotVersion` / `ProviderIds` / `TryGet` / `GetModels`） | 账户、凭据、HTTP |
| `CapabilityPolicy` | 按 `ModelCapabilities` 校验/裁剪请求参数 | 凭据、传输细节 |
| `IChatClientFactory`（`ProviderFactory`） | 按 `providerId + accountId + modelId` 组装 `IChatModel` | 线格式（归 Protocol）、快照内容（归 Catalog） |
| `ICredentialProvider` | `accountId` → 请求 headers | 模型能力、目录 |
| `IChatTransport` | POST 与 SSE 原始行 | 模型目录与能力 |
| `IChatProtocol` | 契约类型 ↔ wire JSON；保留固定端点协议兼容接口 | 凭据、账户、HTTP 实现 |
| `IChatBackend` | 异步准备请求端点、headers 和 body；解析响应并创建流解析器 | HTTP 发送、目录快照维护 |
| `IChatStreamParser` | 单次流中的 SSE 解析、事件组装和结束检查 | 凭据获取、HTTP 发送 |

### 公共 API 风格

对外 .NET 友好：公共签名只使用 class / interface / enum / `Task` / `IAsyncEnumerable` / `IReadOnlyList` / 可空引用（null 表示缺省），不出现 F# DU / option / Result / list / currying；内部实现自由使用 F# 函数式风格。

### 兼容性与依赖

- 保持 net8.0 与 .NET 友好的公开签名；新增能力优先使用新接口、属性和重载，保留现有调用方式。
- 实现或重构必须保持 Microsoft.Extensions.AI 支持，包括 `AsIChatClient()` 的非流式、流式及多轮工具调用。
- 除现有 `Microsoft.Extensions.AI.Abstractions` 外，不新增第三方运行时依赖；HTTP 与 JSON 使用 .NET 自带能力。
- 订阅认证从首次登录到推理均直接通过 HTTP 实现；禁止依赖官方 CLI、启动其进程、加载其 SDK/二进制，或读写其凭据文件。浏览器交互通过宿主委托提供。
- 模型目录的普通构建、查询与运行保持离线；显式目录更新才访问外部数据源。
- 账户在线模型发现必须由宿主显式调用，与离线快照分离；不能将快照列表冒充账户模型权限，也不能通过模型名称猜测专用协议。
- Kimi Code 默认走独立 `kimi-code` Messages 后端；原始 Chat Completions 路径只作为显式兼容选项，不能把文本工具标签当作结构化工具调用成功。

### 凭据与请求作用域

- 创建工厂客户端时不获取凭据或访问网络；发出请求时异步获取当前账户凭据，避免同步阻塞等待异步认证。
- 项目、区域、凭据文件路径属于配置，不能当作 API Key；凭据和令牌存储按账户隔离。
- 默认 OAuth 存储只保存在内存，持久化由宿主通过存储接口实现。
- Google OAuth Client ID 与 Client Secret 由宿主显式配置；禁止在源码、文档、测试及 Git 历史中加入真实凭据。测试使用明确的虚构值。
- 通用订阅会话按 provider、认证域/区域和 accountId 隔离；API key、上游登录令牌和短期推理令牌保持不同语义，不伪造刷新能力或长期有效期。多个进程共享持久化存储的互斥由宿主实现。
- 认证错误仅暴露白名单错误码和状态，不输出 token、授权码或完整认证响应；取消须原样传播。仅可刷新的认证支持一次 401 重试，且流中输出首事件后禁止重放；403 不重试认证。
- 请求使用独立的模型和解析上下文；同一客户端的并发请求、同一流的不同枚举不能共享可变组装状态。

### 流式与多轮状态

- SSE 帧允许产生零到多个事件，处理多行 data、末帧刷新、错误和异常中断，终止事件只输出一次。
- Codex 完整响应必须使用请求作用域的 SSE 解析器聚合，读取服务端终止响应以保留 ID、用量、完整工具参数和 reasoning 状态；截断和错误不能返回部分成功。`IStreamingCompletionBackend` 标志须穿过工厂装饰器，普通 Responses API 不自动切换流式。
- 工具参数增量按调用索引或 ID 组装；交给 MEAI 的 `FunctionCallContent` 必须包含完整参数，不能将不完整 JSON 静默替换为空对象。
- MEAI 聚合后的最终用量只计一次；取消和提前释放应传到上游并完成资源清理。
- `ProviderStateJson` 与 MEAI 消息元数据必须保留原始有序内容、thought signature、thinking 块和受保护 reasoning 状态，并按协议及请求模型隔离。
- 工具 schema 的转换应保留语义；无法表示的约束明确报错，不静默丢弃验证条件。

### 协议扩展点

`ProviderDefinition.ProtocolId` 是开放字符串，内置协议见后端支持表。原有 `IChatProtocol` 继续支持固定端点的自定义客户端；需要动态端点、异步凭据或多事件流解析时，实现 `IChatBackend` / `IChatStreamParser` 并传给 `ChatClient(model, backend, transport)`。工厂使用目录协议、注册表与显式 `ProviderOptions.ProtocolId` 选择已内置的后端。

## 验证

```powershell
dotnet build FSharp.ModelsDev.slnx -c Release
dotnet test FSharp.ModelsDev.slnx -c Release --no-build
dotnet run --project tools/FSharp.ModelsDev.Catalog.Generator -- verify
```

默认运行离线测试，并跳过真实接口测试。显式设置 `MODELSDEV_LIVE_TESTS=1` 后，配置对应测试变量；每个真实接口测试执行一次非流式和一次流式请求：

| 接口 | 必需变量 |
| --- | --- |
| OpenAI Chat / Responses | `MODELSDEV_OPENAI_KEY`、`MODELSDEV_OPENAI_MODEL` |
| Gemini | `MODELSDEV_GEMINI_KEY`、`MODELSDEV_GEMINI_MODEL` |
| Vertex Gemini | `MODELSDEV_VERTEX_TOKEN`、`MODELSDEV_VERTEX_MODEL`、`GOOGLE_VERTEX_PROJECT`、`GOOGLE_VERTEX_LOCATION` |
| Anthropic | `MODELSDEV_ANTHROPIC_KEY`、`MODELSDEV_ANTHROPIC_MODEL` |
| Vertex Claude | `MODELSDEV_VERTEX_TOKEN`、`MODELSDEV_VERTEX_CLAUDE_MODEL`、`GOOGLE_VERTEX_PROJECT`、`GOOGLE_VERTEX_LOCATION` |
| Antigravity | `MODELSDEV_ANTIGRAVITY_TOKEN`、`MODELSDEV_ANTIGRAVITY_MODEL`、`MODELSDEV_ANTIGRAVITY_PROJECT` |
| Grok 订阅 | `MODELSDEV_GROK_TOKEN`、`MODELSDEV_GROK_MODEL` |
| OpenRouter | `MODELSDEV_OPENROUTER_TOKEN`（API key）、`MODELSDEV_OPENROUTER_MODEL` |
| Kimi Code 国内入口 | `MODELSDEV_KIMI_TOKEN`、`MODELSDEV_KIMI_MODEL` |
| Copilot | `MODELSDEV_COPILOT_TOKEN`（上游 GitHub OAuth token）、`MODELSDEV_COPILOT_MODEL` |
| Kilo | `MODELSDEV_KILO_TOKEN`、`MODELSDEV_KILO_MODEL` |
| ClinePass | `MODELSDEV_CLINEPASS_TOKEN`（带 `workos:` 前缀）、`MODELSDEV_CLINEPASS_MODEL` |

订阅测试先显式发现账户模型，再对指定模型执行非流式和流式请求；不读取 CLI 凭据或启动登录。可额外提供对应的 `MODELSDEV_<服务>_REFRESH_TOKEN` 供支持刷新的服务使用；缺少真实凭据时保持跳过，不能将离线测试通过描述为真实服务已验证。

使用 `dotnet test --filter Category=Live` 单独运行。真实接口测试不发起交互式登录。

## 目录维护

models.dev 快照（`catalog/models.snapshot.json` + `.sha256`）作为 EmbeddedResource 编译进程序集，普通构建与运行不联网。

```bash
# 联网更新快照，并重新生成 src/FSharp.ModelsDev/Generated/*.g.fs
dotnet run --project tools/FSharp.ModelsDev.Catalog.Generator -- update-catalog

# 校验快照与生成代码一致（CI 使用）
dotnet run --project tools/FSharp.ModelsDev.Catalog.Generator -- verify
```

更新快照或生成逻辑后，确保快照、校验和与生成索引保持一致，并运行 `verify`；生成文件的变更应来自生成器。
