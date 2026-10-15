namespace FSharp.ModelsDev.Providers

open System
open System.Collections.Generic
open System.Globalization
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog

module private GitHubCopilotAuthHelpers =
    let str (element: JsonElement) (name: string) (fallback: string) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.String then value.GetString() else fallback
    let int64 (element: JsonElement) (name: string) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then
            match value.TryGetInt64() with true, n -> Nullable n | _ -> Nullable()
        else Nullable()
    let expiry (element: JsonElement) (name: string) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then
            match value.ValueKind with
            | JsonValueKind.Number ->
                match value.TryGetInt64() with true, n when n > 0L -> Nullable(DateTimeOffset.FromUnixTimeSeconds(n)) | _ -> Nullable()
            | JsonValueKind.String ->
                match DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with true, date -> Nullable(date) | _ -> Nullable()
            | _ -> Nullable()
        else Nullable()
    let child (element: JsonElement) (name: string) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then value else Unchecked.defaultof<JsonElement>
    let strings (element: JsonElement) (name: string) =
        let value = child element name
        if value.ValueKind = JsonValueKind.Array then
            value.EnumerateArray() |> Seq.choose (fun item -> if item.ValueKind = JsonValueKind.String then Some(item.GetString()) else None) |> Seq.toArray
        else Array.empty
    let origin (uri: Uri) = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/').ToLowerInvariant()
    let jsonContent (values: (string * string) seq) =
        let body = Dictionary<string, string>(StringComparer.Ordinal)
        for key, value in values do body.[key] <- value
        new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    let isTrue (element: JsonElement) name =
        let value = child element name
        value.ValueKind = JsonValueKind.True

[<Sealed>]
type internal GitHubCopilotAuthAdapter(options: ProviderAuthOptions, http: HttpClient, clock: TimeProvider) =
    let options = if isNull options then ProviderAuthOptions.GitHubCopilot() else options
    let http = if isNull http then new HttpClient() else http
    let clock = if isNull clock then TimeProvider.System else clock
    let providerId = "github-copilot"
    let authority = options.Authority
    let clientId = if String.IsNullOrWhiteSpace options.ClientId then "Iv1.b507a08c87ecfe98" else options.ClientId
    let normalizeEnterpriseDomain (raw: string) =
        let value = if String.IsNullOrWhiteSpace raw then "github.com" else raw.Trim()
        let candidate = if value.Contains("://", StringComparison.Ordinal) then value else "https://" + value
        match Uri.TryCreate(candidate, UriKind.Absolute) with
        | true, uri when uri.Scheme = "https" && String.IsNullOrWhiteSpace uri.UserInfo && uri.Port = 443 && uri.AbsolutePath = "/" && String.IsNullOrEmpty uri.Query && String.IsNullOrEmpty uri.Fragment -> uri.IdnHost.ToLowerInvariant()
        | _ -> raise (AiException("GitHub Copilot EnterpriseDomain must be a hostname or HTTPS origin without a path."))
    let enterpriseDomain = normalizeEnterpriseDomain options.EnterpriseDomain
    let isPublic = String.Equals(enterpriseDomain, "github.com", StringComparison.OrdinalIgnoreCase)
    let defaultIssuer = "https://" + enterpriseDomain
    let issuer = if String.IsNullOrWhiteSpace options.Issuer || (not isPublic && options.Issuer.TrimEnd('/').Equals("https://github.com", StringComparison.OrdinalIgnoreCase)) then defaultIssuer else options.Issuer.TrimEnd('/')
    let defaultDevice = defaultIssuer + "/login/device/code"
    let defaultToken = defaultIssuer + "/login/oauth/access_token"
    let defaultCopilotToken =
        if isPublic then "https://api.github.com/copilot_internal/v2/token"
        else "https://api." + enterpriseDomain + "/copilot_internal/v2/token"
    let defaultApi = if isPublic then "https://api.githubcopilot.com" else "https://copilot-api." + enterpriseDomain
    let deviceEndpoint = if String.IsNullOrWhiteSpace options.DeviceAuthorizationEndpoint || (not isPublic && options.DeviceAuthorizationEndpoint.TrimEnd('/').Equals("https://github.com/login/device/code", StringComparison.OrdinalIgnoreCase)) then defaultDevice else options.DeviceAuthorizationEndpoint
    let tokenEndpoint = if String.IsNullOrWhiteSpace options.TokenEndpoint || (not isPublic && options.TokenEndpoint.TrimEnd('/').Equals("https://github.com/login/oauth/access_token", StringComparison.OrdinalIgnoreCase)) then defaultToken else options.TokenEndpoint
    let copilotTokenEndpoint = if String.IsNullOrWhiteSpace options.CopilotTokenEndpoint || (not isPublic && options.CopilotTokenEndpoint.TrimEnd('/').Equals("https://api.github.com/copilot_internal/v2/token", StringComparison.OrdinalIgnoreCase)) then defaultCopilotToken else options.CopilotTokenEndpoint
    let apiBase = if String.IsNullOrWhiteSpace options.ApiBaseUrl || (not isPublic && options.ApiBaseUrl.TrimEnd('/').Equals("https://api.githubcopilot.com", StringComparison.OrdinalIgnoreCase)) then defaultApi else options.ApiBaseUrl.TrimEnd('/')
    let trustedOrigins =
        let configured =
            if isNull options.TrustedOrigins then Seq.empty
            else options.TrustedOrigins :> seq<string>
        seq {
            yield "https://github.com"
            yield "https://api.github.com"
            yield "https://api.githubcopilot.com"
            yield "https://copilot-api.github.com"
            if not isPublic then
                yield "https://" + enterpriseDomain
                yield "https://api." + enterpriseDomain
                yield "https://copilot-api." + enterpriseDomain
            yield! configured
        }
        |> Seq.choose (fun value ->
            match Uri.TryCreate(value, UriKind.Absolute) with
            | true, uri when uri.Scheme = "https" && String.IsNullOrWhiteSpace uri.UserInfo && uri.AbsolutePath = "/" && String.IsNullOrEmpty uri.Query && String.IsNullOrEmpty uri.Fragment -> Some(GitHubCopilotAuthHelpers.origin uri)
            | _ -> None)
        |> fun values -> HashSet<string>(values, StringComparer.OrdinalIgnoreCase)
    let isFirstPartyHost (host: string) =
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("githubcopilot.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".githubcopilot.com", StringComparison.OrdinalIgnoreCase)
    let requireTrustedEndpoint label (value: string) =
        match Uri.TryCreate(value, UriKind.Absolute) with
        | true, uri when uri.Scheme = "https" && String.IsNullOrWhiteSpace uri.UserInfo && (isFirstPartyHost uri.IdnHost || trustedOrigins.Contains(GitHubCopilotAuthHelpers.origin uri)) -> uri
        | _ -> raise (AiException($"GitHub Copilot {label} endpoint is outside the configured trusted origins."))
    let withHeaders (request: HttpRequestMessage) (includeEditorHeaders: bool) =
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
        request.Headers.UserAgent.ParseAdd("GitHubCopilotChat/0.35.0")
        if includeEditorHeaders then
            request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.107.0") |> ignore
            request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.35.0") |> ignore
            request.Headers.TryAddWithoutValidation("Copilot-Integration-Id", "vscode-chat") |> ignore
    let readBody (response: HttpResponseMessage) ct = task {
        let! body = response.Content.ReadAsStringAsync(ct)
        if not response.IsSuccessStatusCode then
            return raise (AiException($"GitHub Copilot authentication request failed with HTTP {int response.StatusCode}.", int response.StatusCode))
        else
            try return JsonDocument.Parse(body)
            with _ -> return raise (AiException("GitHub Copilot returned an invalid authentication response."))
    }
    let makeSession accountId (upstreamToken: string) =
        if String.IsNullOrWhiteSpace upstreamToken then raise (AiException("GitHub device authorization did not return an access token."))
        let session = ProviderAuthSession(providerId, authority, accountId)
        session.CredentialKind <- ProviderCredentialKind.Copilot
        session.UpstreamToken <- upstreamToken
        // This marks the Copilot token exchange as immediately due; it is not an upstream expiry value.
        session.ExpiresAt <- Nullable(clock.GetUtcNow())
        session.ApiBaseUrl <- apiBase
        session.DeviceId <- options.DeviceId
        session
    interface IProviderAuthAdapter with
        member _.SupportsBrowser = false
        member _.SupportsDevice = true
        member _.CanRefresh = true
        member _.BeginBrowserLoginAsync(ct) = Task.FromException<ProviderBrowserLogin>(NotSupportedException("GitHub Copilot uses device authorization."))
        member _.ExchangeBrowserCodeAsync(_, _, _, _) = Task.FromException<ProviderAuthSession>(NotSupportedException("GitHub Copilot uses device authorization."))
        member _.BeginDeviceLoginAsync(ct) = task {
            ct.ThrowIfCancellationRequested()
            let endpoint = requireTrustedEndpoint "device authorization" deviceEndpoint
            use request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            withHeaders request false
            request.Content <- GitHubCopilotAuthHelpers.jsonContent [ "client_id", clientId; "scope", "read:user" ]
            use! response = http.SendAsync(request, ct)
            use! doc = readBody response ct
            let root = doc.RootElement
            let deviceCode = GitHubCopilotAuthHelpers.str root "device_code" ""
            let userCode = GitHubCopilotAuthHelpers.str root "user_code" ""
            let verification = GitHubCopilotAuthHelpers.str root "verification_uri" ""
            if String.IsNullOrWhiteSpace deviceCode || String.IsNullOrWhiteSpace userCode || String.IsNullOrWhiteSpace verification then
                raise (AiException("GitHub Copilot device authorization response was incomplete."))
            let verificationUri =
                match Uri.TryCreate(verification, UriKind.Absolute) with
                | true, uri when uri.Scheme = "https" -> uri
                | _ -> raise (AiException("GitHub returned an unsafe device verification URL."))
            let expiry = GitHubCopilotAuthHelpers.int64 root "expires_in"
            let interval = GitHubCopilotAuthHelpers.int64 root "interval"
            let expiresIn = if expiry.HasValue && expiry.Value > 0L then min expiry.Value 3600L else 900L
            let pollEvery = if interval.HasValue && interval.Value > 0L then interval.Value else 5L
            return ProviderDeviceAuthorization(deviceCode, userCode, verificationUri, verificationUri, clock.GetUtcNow().AddSeconds(float expiresIn), TimeSpan.FromSeconds(float pollEvery))
        }
        member _.PollDeviceLoginAsync(device, accountId, ct) = task {
            ct.ThrowIfCancellationRequested()
            if isNull device || clock.GetUtcNow() >= device.ExpiresAt then return Expired
            else
                let endpoint = requireTrustedEndpoint "token" tokenEndpoint
                use request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                withHeaders request false
                request.Content <- GitHubCopilotAuthHelpers.jsonContent
                    [ "client_id", clientId
                      "device_code", device.DeviceCode
                      "grant_type", "urn:ietf:params:oauth:grant-type:device_code" ]
                use! response = http.SendAsync(request, ct)
                let! body = response.Content.ReadAsStringAsync(ct)
                let parsed =
                    try JsonDocument.Parse(body)
                    with _ -> raise (AiException($"GitHub Copilot device authorization failed with HTTP {int response.StatusCode}.", int response.StatusCode))
                use parsed = parsed
                let root = parsed.RootElement
                let error = GitHubCopilotAuthHelpers.str root "error" ""
                if error = "authorization_pending" then return Pending
                elif error = "slow_down" then return SlowDown
                elif error = "access_denied" || error = "bad_verification_code" then return Denied
                elif error = "expired_token" then return Expired
                elif response.IsSuccessStatusCode then
                    let token = GitHubCopilotAuthHelpers.str root "access_token" ""
                    if String.IsNullOrWhiteSpace token then raise (AiException("GitHub device token response did not contain an access token."))
                    return Authorized(makeSession accountId token)
                else return raise (AiException($"GitHub Copilot device authorization failed with HTTP {int response.StatusCode}.", int response.StatusCode))
        }
        member _.RefreshAsync(session, ct) = task {
            if isNull session || session.ProviderId <> providerId || session.Authority <> authority || String.IsNullOrWhiteSpace session.AccountId then
                invalidArg "session" "GitHub Copilot session identity did not match this adapter."
            if String.IsNullOrWhiteSpace session.UpstreamToken then raise (AiException("GitHub Copilot session has no upstream OAuth token. Sign in again."))
            let endpoint = requireTrustedEndpoint "Copilot token" copilotTokenEndpoint
            use request = new HttpRequestMessage(HttpMethod.Get, endpoint)
            withHeaders request true
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", session.UpstreamToken)
            use! response = http.SendAsync(request, ct)
            use! doc = readBody response ct
            let root = doc.RootElement
            let token = GitHubCopilotAuthHelpers.str root "token" ""
            if String.IsNullOrWhiteSpace token then raise (AiException("GitHub Copilot token response did not contain a token."))
            let endpoints = GitHubCopilotAuthHelpers.child root "endpoints"
            let dynamicApi = GitHubCopilotAuthHelpers.str endpoints "api" ""
            if String.IsNullOrWhiteSpace dynamicApi then raise (AiException("GitHub Copilot token response did not include endpoints.api."))
            let trustedApi = requireTrustedEndpoint "API" dynamicApi
            let updated = ProviderAuthSession(providerId, authority, session.AccountId)
            updated.CredentialKind <- ProviderCredentialKind.Copilot
            updated.UpstreamToken <- session.UpstreamToken
            updated.AccessToken <- token
            updated.ApiBaseUrl <- trustedApi.ToString().TrimEnd('/')
            updated.DeviceId <- session.DeviceId
            updated.OrganizationId <- session.OrganizationId
            let expiresAt = GitHubCopilotAuthHelpers.expiry root "expires_at"
            if not expiresAt.HasValue then raise (AiException("GitHub Copilot token response did not include a valid expires_at value."))
            updated.ExpiresAt <- expiresAt
            return updated
        }
        member _.DiscoverModelsAsync(session, ct) = task {
            if isNull session || session.ProviderId <> providerId || session.Authority <> authority || String.IsNullOrWhiteSpace session.AccountId then
                invalidArg "session" "GitHub Copilot session identity did not match this adapter."
            let baseUrl = if String.IsNullOrWhiteSpace session.ApiBaseUrl then apiBase else session.ApiBaseUrl.TrimEnd('/')
            let baseUri = requireTrustedEndpoint "API" baseUrl
            if String.IsNullOrWhiteSpace session.AccessToken then raise (AiException("GitHub Copilot session has no access token."))
            use request = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/models")
            withHeaders request true
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", session.AccessToken)
            use! response = http.SendAsync(request, ct)
            use! doc = readBody response ct
            let root = doc.RootElement
            let data = GitHubCopilotAuthHelpers.child root "data"
            let models = if root.ValueKind = JsonValueKind.Array then root else if data.ValueKind = JsonValueKind.Array then data else GitHubCopilotAuthHelpers.child root "models"
            if models.ValueKind <> JsonValueKind.Array then return raise (AiException("GitHub Copilot model discovery response did not contain a model array."))
            else
                let results = ResizeArray<ProviderModelDescriptor>()
                for item in models.EnumerateArray() do
                    let id = GitHubCopilotAuthHelpers.str item "id" ""
                    if not (String.IsNullOrWhiteSpace id) then
                        let endpoints = GitHubCopilotAuthHelpers.strings item "supported_endpoints"
                        let protocol =
                            if endpoints |> Array.exists (fun x -> x.Contains("responses", StringComparison.OrdinalIgnoreCase)) then "openai-responses"
                            elif endpoints |> Array.exists (fun x -> x.Contains("chat", StringComparison.OrdinalIgnoreCase)) then "openai-chat"
                            elif endpoints |> Array.exists (fun x -> x.Contains("messages", StringComparison.OrdinalIgnoreCase)) then "anthropic-messages"
                            else "openai-chat"
                        let capability = GitHubCopilotAuthHelpers.child item "capabilities"
                        let mutable flags = ModelCapabilities.TextInput
                        let inputs = GitHubCopilotAuthHelpers.strings item "input_modalities"
                        let supports = GitHubCopilotAuthHelpers.child capability "supports"
                        if GitHubCopilotAuthHelpers.isTrue capability "supports_vision" || GitHubCopilotAuthHelpers.isTrue supports "vision" || inputs |> Array.exists (fun x -> x.Equals("image", StringComparison.OrdinalIgnoreCase)) then flags <- flags ||| ModelCapabilities.ImageInput
                        if GitHubCopilotAuthHelpers.isTrue capability "supports_tools" || GitHubCopilotAuthHelpers.isTrue capability "tool_calling" || GitHubCopilotAuthHelpers.isTrue supports "tool_calls" then flags <- flags ||| ModelCapabilities.ToolCalling
                        if GitHubCopilotAuthHelpers.isTrue capability "supports_reasoning" || GitHubCopilotAuthHelpers.isTrue capability "reasoning" || GitHubCopilotAuthHelpers.isTrue supports "reasoning" then flags <- flags ||| ModelCapabilities.Reasoning
                        if GitHubCopilotAuthHelpers.isTrue capability "supports_structured_output" || GitHubCopilotAuthHelpers.isTrue supports "structured_output" then flags <- flags ||| ModelCapabilities.StructuredOutput
                        if GitHubCopilotAuthHelpers.isTrue capability "supports_audio" || GitHubCopilotAuthHelpers.isTrue supports "audio" || inputs |> Array.exists (fun x -> x.Equals("audio", StringComparison.OrdinalIgnoreCase)) then flags <- flags ||| ModelCapabilities.AudioInput
                        let limits = GitHubCopilotAuthHelpers.child item "limits"
                        let context =
                            let value = GitHubCopilotAuthHelpers.int64 limits "max_context_window_tokens"
                            if value.HasValue then value else GitHubCopilotAuthHelpers.int64 item "context_length"
                        let maximum =
                            let value = GitHubCopilotAuthHelpers.int64 limits "max_output_tokens"
                            if value.HasValue then value else GitHubCopilotAuthHelpers.int64 item "max_output_tokens"
                        let displayName = GitHubCopilotAuthHelpers.str item "name" id
                        let efforts =
                            let available = GitHubCopilotAuthHelpers.strings item "reasoning_efforts"
                            if available.Length > 0 then available else GitHubCopilotAuthHelpers.strings item "supported_reasoning_efforts"
                        let descriptor = ProviderModelDescriptor(providerId, id, protocol, baseUrl)
                        descriptor.AccountId <- session.AccountId
                        descriptor.Authority <- authority
                        descriptor.SupportedEndpoints <- endpoints
                        descriptor.ReasoningEfforts <- efforts
                        if GitHubCopilotAuthHelpers.isTrue capability "supports_thinking_toggle" then descriptor.SupportsThinkingToggle <- true
                        descriptor.AlwaysThinking <- GitHubCopilotAuthHelpers.isTrue capability "always_thinking"
                        let caps = flags
                        descriptor.ModelInfo <- ModelInfo(providerId, id, displayName, id, caps, context, maximum, efforts)
                        results.Add descriptor
                return results.ToArray() :> IReadOnlyList<ProviderModelDescriptor>
        }
