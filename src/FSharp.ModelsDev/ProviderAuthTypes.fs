namespace FSharp.ModelsDev.Providers

open System
open System.Collections.Generic
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog

type ProviderCredentialKind =
    | OAuth = 0
    | ApiKey = 1
    | Copilot = 2

[<Sealed; AllowNullLiteral>]
type ProviderAuthSession(providerId: string, authority: string, accountId: string) =
    member _.ProviderId = providerId
    member _.Authority = authority
    member _.AccountId = accountId
    member val CredentialKind = ProviderCredentialKind.OAuth with get, set
    member val AccessToken: string = null with get, set
    member val RefreshToken: string = null with get, set
    /// Long-lived upstream login credential, distinct from a short-lived inference token.
    member val UpstreamToken: string = null with get, set
    member val ExpiresAt = Nullable<DateTimeOffset>() with get, set
    member val ApiBaseUrl: string = null with get, set
    member val DeviceId: string = null with get, set
    member val OrganizationId: string = null with get, set

[<AllowNullLiteral>]
type IProviderAuthSessionStore =
    abstract LoadAsync: providerId: string * authority: string * accountId: string * cancellationToken: CancellationToken -> Task<ProviderAuthSession>
    abstract SaveAsync: session: ProviderAuthSession * cancellationToken: CancellationToken -> Task
    abstract DeleteAsync: providerId: string * authority: string * accountId: string * cancellationToken: CancellationToken -> Task

[<Sealed; AllowNullLiteral>]
type ProviderModelDescriptor(providerId: string, modelId: string, protocolId: string, baseUrl: string) =
    member _.ProviderId = providerId
    member _.ModelId = modelId
    member _.ProtocolId = protocolId
    member _.BaseUrl = baseUrl
    member val AccountId: string = null with get, set
    member val Authority: string = null with get, set
    member val ModelInfo: ModelInfo = null with get, set
    member val SupportedEndpoints: IReadOnlyList<string> = Array.empty with get, set
    member val ReasoningEfforts: IReadOnlyList<string> = Array.empty with get, set
    member val SupportsThinkingToggle = false with get, set
    member val AlwaysThinking = false with get, set

[<Sealed; AllowNullLiteral>]
type ProviderAuthOptions(providerId: string) =
    member _.ProviderId = providerId
    member val ClientId: string = null with get, set
    member val Issuer: string = null with get, set
    member val AuthorizationEndpoint: string = null with get, set
    member val TokenEndpoint: string = null with get, set
    member val CopilotTokenEndpoint: string = null with get, set
    member val DeviceAuthorizationEndpoint: string = null with get, set
    member val ApiBaseUrl: string = null with get, set
    member val RedirectUri = "http://localhost:51122/oauth-callback" with get, set
    member val Scopes: IReadOnlyList<string> = Array.empty with get, set
    member val EnterpriseDomain = "github.com" with get, set
    member val OrganizationId: string = null with get, set
    member val DeviceId: string = null with get, set
    member val ClientVersion = "FSharp.ModelsDev" with get, set
    /// Explicit trusted origins for non-default deployments. Never populated from discovery.
    member val TrustedOrigins: IReadOnlyList<string> = Array.empty with get, set
    member val LoginTimeout = TimeSpan.FromMinutes(10.) with get, set
    member val RefreshSkew = TimeSpan.FromSeconds(60.) with get, set
    /// Includes service endpoint and client identity so regions and enterprise logins cannot collide.
    member this.Authority = String.Join("|", [| this.Issuer; this.ClientId; this.EnterpriseDomain; this.ApiBaseUrl; this.CopilotTokenEndpoint; this.AuthorizationEndpoint; this.TokenEndpoint; this.DeviceAuthorizationEndpoint; this.OrganizationId |])
    static member Grok() =
        let o = ProviderAuthOptions("grok")
        o.Issuer <- "https://auth.x.ai"
        o.ClientId <- "b1a00492-073a-47ea-816f-4c329264a828"
        o.AuthorizationEndpoint <- "https://auth.x.ai/oauth2/authorize"
        o.TokenEndpoint <- "https://auth.x.ai/oauth2/token"
        o.DeviceAuthorizationEndpoint <- "https://auth.x.ai/oauth2/device/code"
        o.ApiBaseUrl <- "https://cli-chat-proxy.grok.com/v1"
        o.Scopes <- [| "openid"; "profile"; "email"; "offline_access"; "grok-cli:access"; "api:access" |]
        o
    static member OpenRouter() =
        let o = ProviderAuthOptions("openrouter")
        o.Issuer <- "https://openrouter.ai"
        o.AuthorizationEndpoint <- "https://openrouter.ai/auth"
        o.TokenEndpoint <- "https://openrouter.ai/api/v1/auth/keys"
        o.ApiBaseUrl <- "https://openrouter.ai/api/v1"
        o
    static member KimiCode(globalRegion: bool) =
        let o = ProviderAuthOptions(if globalRegion then "kimi-code-plan-global" else "kimi-code-plan-cn")
        let host = if globalRegion then "kimi.ai" else "kimi.com"
        o.Issuer <- "https://auth." + host
        o.ClientId <- "17e5f671-d194-4dfb-9706-5516cb48c098"
        o.TokenEndpoint <- o.Issuer + "/api/oauth/token"
        o.DeviceAuthorizationEndpoint <- o.Issuer + "/api/oauth/device_authorization"
        o.ApiBaseUrl <- "https://api." + host + "/coding/v1"
        o.ClientVersion <- "1.49.0"
        o
    static member GitHubCopilot() =
        let o = ProviderAuthOptions("github-copilot")
        o.Issuer <- "https://github.com"
        o.ClientId <- "Iv1.b507a08c87ecfe98"
        o.TokenEndpoint <- "https://github.com/login/oauth/access_token"
        o.CopilotTokenEndpoint <- "https://api.github.com/copilot_internal/v2/token"
        o.DeviceAuthorizationEndpoint <- "https://github.com/login/device/code"
        o.ApiBaseUrl <- "https://api.githubcopilot.com"
        o.Scopes <- [| "read:user" |]
        o
    static member Kilo() =
        let o = ProviderAuthOptions("kilo")
        o.Issuer <- "https://api.kilo.ai"
        o.DeviceAuthorizationEndpoint <- "https://api.kilo.ai/api/device-auth/codes"
        o.ApiBaseUrl <- "https://api.kilo.ai/api/gateway"
        o
    static member ClinePass() =
        let o = ProviderAuthOptions("cline-pass")
        o.Issuer <- "https://api.workos.com"
        o.ClientId <- "client_01K3A541FN8TA3EPPHTD2325AR"
        o.DeviceAuthorizationEndpoint <- "https://api.workos.com/user_management/authorize/device"
        o.TokenEndpoint <- "https://api.workos.com/user_management/authenticate"
        o.ApiBaseUrl <- "https://api.cline.bot/api/v1"
        o

[<Sealed; AllowNullLiteral>]
type ProviderBrowserLogin internal (authorizationUrl: Uri, state: string, verifier: string, redirectUri: string, expiresAt: DateTimeOffset) =
    let mutable consumed = 0
    member _.AuthorizationUrl = authorizationUrl
    member _.ExpiresAt = expiresAt
    member internal _.State = state
    member internal _.Verifier = verifier
    member internal _.RedirectUri = redirectUri
    member internal _.TryConsume() = Interlocked.CompareExchange(&consumed, 1, 0) = 0

[<Sealed; AllowNullLiteral>]
type ProviderDeviceAuthorization internal (deviceCode: string, userCode: string, verificationUri: Uri, verificationUriComplete: Uri, expiresAt: DateTimeOffset, interval: TimeSpan) =
    member internal _.DeviceCode = deviceCode
    member _.UserCode = userCode
    member _.VerificationUri = verificationUri
    member _.VerificationUriComplete = verificationUriComplete
    member _.ExpiresAt = expiresAt
    member _.Interval = interval

[<AllowNullLiteral>]
type IRefreshableCredentialProvider =
    inherit ICredentialProvider
    abstract CanRefresh: bool
    abstract Invalidate: accountId: string -> unit

[<AllowNullLiteral>]
type IProviderAuthSessionProvider =
    abstract GetSessionAsync: accountId: string * cancellationToken: CancellationToken -> Task<ProviderAuthSession>

type internal DevicePollResult =
    | Pending
    | SlowDown
    | Authorized of ProviderAuthSession
    | Denied
    | Expired

type internal IProviderAuthAdapter =
    abstract SupportsBrowser: bool
    abstract SupportsDevice: bool
    abstract CanRefresh: bool
    abstract BeginBrowserLoginAsync: cancellationToken: CancellationToken -> Task<ProviderBrowserLogin>
    /// Receives a validated authorization code, never a raw callback URL.
    abstract ExchangeBrowserCodeAsync: login: ProviderBrowserLogin * code: string * accountId: string * cancellationToken: CancellationToken -> Task<ProviderAuthSession>
    abstract BeginDeviceLoginAsync: cancellationToken: CancellationToken -> Task<ProviderDeviceAuthorization>
    abstract PollDeviceLoginAsync: device: ProviderDeviceAuthorization * accountId: string * cancellationToken: CancellationToken -> Task<DevicePollResult>
    abstract RefreshAsync: session: ProviderAuthSession * cancellationToken: CancellationToken -> Task<ProviderAuthSession>
    abstract DiscoverModelsAsync: session: ProviderAuthSession * cancellationToken: CancellationToken -> Task<IReadOnlyList<ProviderModelDescriptor>>
