# 仓库开发指南

本文件适用于整个仓库，供修改代码、测试和维护目录时使用。SDK 的配置与使用示例见 [README.md](README.md)。

README.md 为默认英文说明，README.zh-CN.md 为对应中文版；公共 API、示例和能力范围应保持一致。README 面向包使用者，仓库约束、CI、发布及维护步骤留在本文件。

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
│   ├── CatalogTypes.fs              #   ModelInfo / ModelMetadata / ModelFilter / ProviderDefinition / ModelCapabilities
│   ├── IModelCatalog.fs             #   IModelCatalog（纯离线目录 seam）
│   ├── ModelCatalogQuery.fs         #   目录与模型集合的离线组合筛选
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
| `IModelCatalog` | 快照的纯离线查询（`SnapshotVersion` / `ProviderIds` / `TryGet` / `GetModels`）；`ModelCatalogQuery` 对目录或已有模型集合进行元数据筛选 | 账户、凭据、HTTP |
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

使用 .NET 10 SDK 处理 `.slnx`，安装 .NET 8 运行时执行 `net8.0` 测试。CI 同时安装 .NET 8/10 SDK，在 Linux、Windows 构建和运行默认离线测试；真实接口和录制开关强制关闭。

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

### HTTP/SSE 录制回放

`tests/FSharp.ModelsDev.Tests/Recording/` 是测试专用设施；通过注入 `HttpClient` 的 `HttpChatTransport` 观察实际 HTTP 请求，不能替换后端序列化或流解析。注入的 `HttpClient` 由调用者释放；默认构造方式继续使用共享客户端。样本及来源/许可证位于 `Fixtures/Recordings/`，不编译进 SDK，也不修改 models.dev 目录快照。

- 默认只回放，缺失必需样本、请求不匹配、多余请求、未消费完交互或响应都必须失败，不能自动联网、录制或跳过。每个场景结尾调用 `VerifyComplete()`。
- 按交互顺序匹配 method、URL、允许的 headers 与完整 JSON body；JSON 对象键顺序不重要，数组顺序及字段值必须相同。默认仅保留 `content-type`（规范化媒体类型）和 `anthropic-version`，不以凭据值参与匹配。
- 录制须同时显式启用 `MODELSDEV_RECORD=1`、`MODELSDEV_LIVE_TESTS=1`，提供 `MODELSDEV_RECORDINGS_DIR` 及对应服务的 `MODELSDEV_<服务>_KEY/MODEL`。禁止在 CI/GITHUB_ACTIONS 环境录制，不覆盖已有文件；刷新时仅删除明确选中的样本后重新录制。
- 凭据只能由宿主通过环境变量提供，不读取官方 CLI 凭据、不启动 CLI。录制工具脱敏认证 headers、URL/form 授权参数及 JSON/SSE 中的令牌和已知密钥；错误诊断不得回显请求 body 或凭据。
- 成功执行断言、完整读取全部响应后才显式 `Save()`，原子创建录制文件；失败或取消不保存。提交前人工审阅样本、metadata 和 `sourceRequest`，自动脱敏不能替代审阅。
- 当前支持 UTF-8 HTTP/JSON/SSE，不支持二进制/WebSocket。录制缓冲完整响应，回放人为分片字节但不模拟网络时序；超时、半途取消、阻塞读取等仍由独立传输测试覆盖。
- 上游样本须固定来源提交并保留许可证；请求适配须明确记录，原始响应及来源 SHA-256 不得为了让测试通过而重写。上游样本回放通过不能描述为本库已通过真实服务验证。

```powershell
# 离线运行基础设施、四组上游样本及已有 local/ 样本。
dotnet test FSharp.ModelsDev.slnx -c Release --filter Category=Recorded

# 显式录制 OpenAI Chat 与 Responses（每个场景一次完整调用、一次流式调用）。
$env:MODELSDEV_RECORD = "1"
$env:MODELSDEV_LIVE_TESTS = "1"
$env:MODELSDEV_RECORDINGS_DIR = Join-Path (Get-Location) "tests/FSharp.ModelsDev.Tests/Fixtures/Recordings"
# MODELSDEV_OPENAI_KEY、MODELSDEV_OPENAI_MODEL 由宿主预先设置。
dotnet test FSharp.ModelsDev.slnx -c Release --filter 'Category=Recording&FullyQualifiedName~OpenAI'

# 关闭录制；审阅新增 local/*.json 后离线验证。
Remove-Item Env:MODELSDEV_RECORD
Remove-Item Env:MODELSDEV_LIVE_TESTS
dotnet test FSharp.ModelsDev.slnx -c Release --filter Category=Recorded
```

Anthropic/Gemini 录制使用 `MODELSDEV_ANTHROPIC_KEY/MODEL`、`MODELSDEV_GEMINI_KEY/MODEL`，分别筛选 `FullyQualifiedName~Anthropic` 或 `FullyQualifiedName~Gemini`。只运行已配置的目标服务，新增本地样本通过公共 metadata 场景回放入口验证；手写协议样本应明确标为合成数据。

## 目录维护

models.dev 快照（`catalog/models.snapshot.json` + `.sha256`）作为 EmbeddedResource 编译进程序集，普通构建与运行不联网。

`ModelInfo` 的目录元数据包括 `MaxInputTokens`、输入/输出模态、描述、family、model type、status、knowledge cutoff、发布日期、最后更新时间、附件/温度支持标记及开放权重标记；新增可选元数据应以 nullable 值或空列表表达未知。时间及日期字段保留快照原始字符串（包括部分日期）。`ModelInfo` 的元数据构造重载必须快照复制可变 `ModelMetadata` DTO 及其模态列表，保留旧构造重载，并使旧重载的新属性保持未知。

`ModelCatalogQuery.FindModels` 查询 `IModelCatalog`，`FilterModels` 筛选调用者已有的模型集合；两者都必须离线，不触发快照更新或账户发现。所有非空条件取 AND；provider/family/model type/status 每组标识值取 OR；所需输入/输出模态均须存在；所需能力须全部满足。字符串比较使用 ordinal 语义。指定数值或布尔筛选时，未知值不匹配；空/缺省筛选不限制结果。数值下限允许 0、拒绝负值，输入 token、上下文与最大输出各自按原值筛选，不可互相推算。结果稳定按 provider ID 和 model ID 排序。

快照中的模态或支持标记仅是目录元数据，不代表 SDK 后端/传输实际支持对应功能，也不代表账户权限。账户在线发现仍由宿主显式调用并与离线快照分离，不将发现结果自动合并进快照或其元数据。目录生成与刷新机制保持既有 raw snapshot、校验和及生成索引流程；不得在本功能中加入在线刷新，也不新增价格支持。

```bash
# 联网更新快照，并重新生成 src/FSharp.ModelsDev/Generated/*.g.fs
dotnet run --project tools/FSharp.ModelsDev.Catalog.Generator -- update-catalog

# 校验快照与生成代码一致（CI 使用）
dotnet run --project tools/FSharp.ModelsDev.Catalog.Generator -- verify
```

更新快照或生成逻辑后，确保快照、校验和与生成索引保持一致，并运行 `verify`；生成文件的变更应来自生成器。

## CI 与 NuGet 发布

`.github/workflows/ci.yml` 响应 main 推送、PR、手动运行，并由发布工作流复用。除构建、离线测试和目录校验外，Linux job 打包并通过 `.github/scripts/Test-Package.ps1` 验证 DLL、XML 文档、双语 README、MIT 许可证、版本及符号包，再将产物安装进独立 F#、C# 消费项目。`tests/PackageSmoke.*` 不加入解决方案，不引用 SDK 工程；必须用生成的 NuGet 包验证，以免项目引用掩盖打包遗漏。使用独立依赖缓存和 source mapping，确保本库只能从本次产物源安装。

发布只由 `.github/workflows/publish.yml` 的版本 tag 推送触发。格式为纯三段数字 `MAJOR.MINOR.PATCH`，例如 `0.1.0`；禁止 `v` 前缀、预发布后缀（如 `rc`）、前导零及 `+build` 元数据。`.github/scripts/Resolve-Version.ps1` 验证并原样返回版本号，该值同时用于构建、打包和发布。不依赖手动修改项目中的默认开发版本。

NuGet 信任发布策略绑定 `kwadraten/FSharp.ModelsDev`、文件名 `publish.yml` 和 GitHub Environment `nuget.org`。发布 job 必须保持这些匹配项，只有该 job 申请 `id-token: write`；`NuGet/login` 的用户名固定为 `kwadraten`，无需长期 API key 或仓库 secret。发布 job 下载同次运行中已通过检查的产物，不重新打包；校验或任何平台测试失败都不能发布。重跑通过 `--skip-duplicate` 避免重复版本报错，NuGet 已发布版本不可覆盖。Actions 固定到完整提交 SHA，升级时核对对应官方 release。

本地检查打包产物：

```powershell
$version = ./.github/scripts/Resolve-Version.ps1 -Tag 0.1.0
dotnet build FSharp.ModelsDev.slnx -c Release -p:Version=$version -p:ContinuousIntegrationBuild=true
dotnet pack src/FSharp.ModelsDev/FSharp.ModelsDev.fsproj -c Release --no-build --no-restore -p:Version=$version -p:ContinuousIntegrationBuild=true -o artifacts/packages
./.github/scripts/Test-Package.ps1 -Version $version
```

构建/打包依赖还原可访问 NuGet；消费项目实际执行的目录查询与客户端创建仍须离线。GitHub OIDC 交换和远端推送只能在实际工作流中验证，不能将本地打包成功描述为发布成功。

确认待发布提交已包含工作流和全部实现后，创建并推送 tag 即触发自动发布：

```powershell
git tag -a 0.1.0 -m "Release 0.1.0"
git push origin main
git push origin 0.1.0
```

只有用户明确要求发布时才实际创建/推送版本 tag。NuGet 机制参考[官方 Trusted Publishing 文档](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)。
