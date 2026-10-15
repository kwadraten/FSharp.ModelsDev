namespace FSharp.ModelsDev.Providers

open System
open System.Collections.Generic
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog

module private KimiCodeAuthJson =
    let tryProperty (element: JsonElement) (name: string) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then Some value else None

    let stringValue (element: JsonElement) (name: string) (fallback: string) =
        match tryProperty element name with
        | Some value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ -> fallback

    let integerValue (element: JsonElement) (name: string) =
        match tryProperty element name with
        | Some value -> match value.TryGetInt64() with true, n -> Nullable n | _ -> Nullable()
        | None -> Nullable()

    let booleanValue (element: JsonElement) (name: string) =
        match tryProperty element name with
        | Some value when value.ValueKind = JsonValueKind.True -> Some true
        | Some value when value.ValueKind = JsonValueKind.False -> Some false
        | _ -> None

    let strings (element: JsonElement) (name: string) =
        match tryProperty element name with
        | Some value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray()
            |> Seq.choose (fun item -> if item.ValueKind = JsonValueKind.String then Some(item.GetString()) else None)
            |> Seq.toArray
        | _ -> Array.empty

    let oauthError (root: JsonElement) =
        let code = stringValue root "error" ""
        match code with
        | "authorization_pending" | "slow_down" | "access_denied" | "expired_token" | "invalid_grant" -> code
        | _ -> "authentication_failed"

/// Shared compatibility identity for Kimi OAuth and inference requests.
/// Values are a .NET-only port of kimi-oauth-bridge's v1.49.0 headers.ts/constants.ts.
module internal KimiCodeHeaders =
    let private ascii (value: string) =
        if isNull value then "unknown"
        else
            let filtered = value |> Seq.filter (fun c -> c >= ' ' && c <= '~') |> Seq.toArray |> fun chars -> String(chars)
            if String.IsNullOrWhiteSpace filtered then "unknown" else filtered.Trim()

    let private machineName () =
        let architecture = Runtime.InteropServices.RuntimeInformation.OSArchitecture
        if OperatingSystem.IsWindows() && architecture = Runtime.InteropServices.Architecture.X64 then "AMD64"
        elif architecture = Runtime.InteropServices.Architecture.X64 then "x86_64"
        elif architecture = Runtime.InteropServices.Architecture.Arm64 then "arm64"
        else architecture.ToString().ToLowerInvariant()

    let private deviceModel () =
        let version = Environment.OSVersion.Version
        let machine = machineName()
        if OperatingSystem.IsWindows() then
            let label = if version.Build >= 22000 then "11" else "10"
            $"Windows {label} {machine}"
        elif OperatingSystem.IsMacOS() then $"macOS {version} {machine}"
        elif OperatingSystem.IsLinux() then $"Linux {version} {machine}"
        else $"{Environment.OSVersion.Platform} {version} {machine}"

    let create (deviceId: string) (clientVersion: string) =
        let version = if String.IsNullOrWhiteSpace clientVersion then "1.49.0" else ascii clientVersion
        let headers = Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        headers["User-Agent"] <- "KimiCLI/" + version
        headers["X-Msh-Platform"] <- "kimi_cli"
        headers["X-Msh-Version"] <- version
        headers["X-Msh-Device-Name"] <- ascii Environment.MachineName
        headers["X-Msh-Device-Model"] <- ascii (deviceModel())
        headers["X-Msh-Device-Id"] <- if String.IsNullOrWhiteSpace deviceId then Guid.NewGuid().ToString("D") else deviceId
        headers["X-Msh-Os-Version"] <- ascii Environment.OSVersion.VersionString
        headers :> IReadOnlyDictionary<string,string>

/// Kimi Code device OAuth and account-scoped model discovery.
[<Sealed>]
type internal KimiCodeAuthAdapter(options: ProviderAuthOptions, injectedHttp: HttpClient, injectedClock: TimeProvider) =
    let options = if isNull options then ProviderAuthOptions.KimiCode(true) else options
    let http = if isNull injectedHttp then new HttpClient() else injectedHttp
    let clock = if isNull injectedClock then TimeProvider.System else injectedClock
    let providerId = options.ProviderId
    let authority = options.Authority
    let issuer = if String.IsNullOrWhiteSpace options.Issuer then "https://auth.kimi.com" else options.Issuer.TrimEnd('/')
    let deviceEndpoint = if String.IsNullOrWhiteSpace options.DeviceAuthorizationEndpoint then issuer + "/api/oauth/device_authorization" else options.DeviceAuthorizationEndpoint
    let tokenEndpoint = if String.IsNullOrWhiteSpace options.TokenEndpoint then issuer + "/api/oauth/token" else options.TokenEndpoint
    let apiBase = if String.IsNullOrWhiteSpace options.ApiBaseUrl then "https://api.kimi.com/coding/v1" else options.ApiBaseUrl.TrimEnd('/')
    let clientId = if String.IsNullOrWhiteSpace options.ClientId then "17e5f671-d194-4dfb-9706-5516cb48c098" else options.ClientId
    let deviceId = if String.IsNullOrWhiteSpace options.DeviceId then Guid.NewGuid().ToString("D") else options.DeviceId
    let clientVersion =
        if String.IsNullOrWhiteSpace options.ClientVersion || options.ClientVersion = "FSharp.ModelsDev" then "1.49.0"
        else options.ClientVersion
    let origin (uri: Uri) = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/').ToLowerInvariant()
    let trustedOrigins =
        HashSet<string>(seq {
            yield "https://auth.kimi.com"
            yield "https://api.kimi.com"
            yield "https://auth.kimi.ai"
            yield "https://api.kimi.ai"
            if not (isNull options.TrustedOrigins) then yield! options.TrustedOrigins
        } |> Seq.choose (fun value -> match Uri.TryCreate(value, UriKind.Absolute) with true, uri when uri.Scheme = "https" -> Some(origin uri) | _ -> None), StringComparer.OrdinalIgnoreCase)
    let requireTrustedEndpoint (label: string) (value: string) =
        match Uri.TryCreate(value, UriKind.Absolute) with
        | true, uri when uri.Scheme = "https" && trustedOrigins.Contains(origin uri) -> uri
        | _ -> raise (AiException($"Kimi Code {label} endpoint must use HTTPS and a trusted origin."))

    let identityHeaders (deviceIdOverride: string) =
        KimiCodeHeaders.create (if String.IsNullOrWhiteSpace deviceIdOverride then deviceId else deviceIdOverride) clientVersion

    let sendForm (allowOAuthError: bool) (endpoint: string) (requestDeviceId: string) (values: (string * string) list) (ct: CancellationToken) = task {
        let endpointUri = requireTrustedEndpoint "OAuth" endpoint
        use request = new HttpRequestMessage(HttpMethod.Post, endpointUri)
        for KeyValue(key, value) in identityHeaders(requestDeviceId) do request.Headers.TryAddWithoutValidation(key, value) |> ignore
        request.Headers.Accept.ParseAdd("application/json")
        request.Content <- new FormUrlEncodedContent(values |> Seq.map (fun (key, value) -> KeyValuePair<string,string>(key, value)))
        use! response = http.SendAsync(request, ct)
        let! body = response.Content.ReadAsStringAsync(ct)
        let parsed =
            try JsonDocument.Parse(body)
            with _ -> raise (AiException($"Kimi Code OAuth returned invalid JSON (HTTP {int response.StatusCode}).", int response.StatusCode))
        let oauthCode = KimiCodeAuthJson.stringValue parsed.RootElement "error" ""
        let expectedPollingError = oauthCode = "authorization_pending" || oauthCode = "slow_down" || oauthCode = "access_denied" || oauthCode = "expired_token"
        if not response.IsSuccessStatusCode && not (allowOAuthError && expectedPollingError) then
            let detail = KimiCodeAuthJson.oauthError parsed.RootElement
            parsed.Dispose()
            raise (AiException($"Kimi Code OAuth failed ({if String.IsNullOrWhiteSpace detail then string (int response.StatusCode) else detail}).", int response.StatusCode))
        return parsed
    }

    let makeSession (accountId: string) (root: JsonElement) =
        let accessToken = KimiCodeAuthJson.stringValue root "access_token" ""
        if String.IsNullOrWhiteSpace accessToken then raise (AiException("Kimi Code token response did not contain an access token."))
        let session = ProviderAuthSession(providerId, authority, accountId)
        session.AccessToken <- accessToken
        session.RefreshToken <- KimiCodeAuthJson.stringValue root "refresh_token" null
        session.ApiBaseUrl <- apiBase
        session.DeviceId <- deviceId
        let expires = KimiCodeAuthJson.integerValue root "expires_in"
        if expires.HasValue && expires.Value > 0L then session.ExpiresAt <- Nullable(clock.GetUtcNow().AddSeconds(float expires.Value))
        session

    let discover (session: ProviderAuthSession) (ct: CancellationToken) = task {
        if isNull session || String.IsNullOrWhiteSpace session.AccessToken then raise (AiException("Kimi Code model discovery requires an authenticated session."))
        let baseUrl = if String.IsNullOrWhiteSpace session.ApiBaseUrl then apiBase else session.ApiBaseUrl.TrimEnd('/')
        let baseUri = requireTrustedEndpoint "API base URL" baseUrl
        use request = new HttpRequestMessage(HttpMethod.Get, Uri(baseUri, baseUri.AbsolutePath.TrimEnd('/') + "/models"))
        request.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", session.AccessToken)
        request.Headers.Accept.ParseAdd("application/json")
        for KeyValue(key, value) in identityHeaders(session.DeviceId) do request.Headers.TryAddWithoutValidation(key, value) |> ignore
        use! response = http.SendAsync(request, ct)
        let! body = response.Content.ReadAsStringAsync(ct)
        if not response.IsSuccessStatusCode then raise (AiException($"Kimi Code model discovery failed with HTTP {int response.StatusCode}.", int response.StatusCode))
        use document =
            try JsonDocument.Parse(body)
            with _ -> raise (AiException("Kimi Code returned invalid model discovery JSON."))
        let data =
            match KimiCodeAuthJson.tryProperty document.RootElement "data" with
            | Some value when value.ValueKind = JsonValueKind.Array -> value
            | _ -> raise (AiException("Kimi Code model discovery response did not contain a data array."))
        if data.ValueKind <> JsonValueKind.Array then return raise (AiException("Kimi Code model discovery response did not contain a data array."))
        else
            let result = ResizeArray<ProviderModelDescriptor>()
            for item in data.EnumerateArray() do
                let id = KimiCodeAuthJson.stringValue item "id" ""
                if String.IsNullOrWhiteSpace id then raise (AiException("Kimi Code model discovery contained an entry without an id."))
                else
                    let displayName = KimiCodeAuthJson.stringValue item "display_name" id
                    let context = KimiCodeAuthJson.integerValue item "context_length"
                    let thinkingType = KimiCodeAuthJson.stringValue item "supports_thinking_type" ""
                    let thinkingEfforts = KimiCodeAuthJson.tryProperty item "think_efforts"
                    let effortSupported, efforts =
                        match thinkingEfforts with
                        | Some effort when effort.ValueKind = JsonValueKind.Object ->
                            let supported = KimiCodeAuthJson.booleanValue effort "support" = Some true
                            supported, (if supported then KimiCodeAuthJson.strings effort "valid_efforts" else Array.empty)
                        | _ -> false, Array.empty
                    let supportsReasoning =
                        KimiCodeAuthJson.booleanValue item "supports_reasoning" = Some true
                        || thinkingType = "only" || thinkingType = "both" || effortSupported
                    // Kimi Code's coding service supports function calls (verified through its
                    // Messages endpoint); dynamic_load is a separate tool-loading feature.
                    let mutable capabilities = ModelCapabilities.TextInput ||| ModelCapabilities.ToolCalling
                    if KimiCodeAuthJson.booleanValue item "supports_tool_use" = Some true then capabilities <- capabilities ||| ModelCapabilities.ToolCalling
                    if KimiCodeAuthJson.booleanValue item "supports_image_in" = Some true then capabilities <- capabilities ||| ModelCapabilities.ImageInput
                    if supportsReasoning then capabilities <- capabilities ||| ModelCapabilities.Reasoning
                    let descriptor = ProviderModelDescriptor(providerId, id, "kimi-code", baseUrl)
                    descriptor.AccountId <- session.AccountId
                    descriptor.Authority <- authority
                    descriptor.SupportedEndpoints <- [| "messages" |]
                    descriptor.ReasoningEfforts <- efforts
                    descriptor.SupportsThinkingToggle <- (thinkingType = "both")
                    descriptor.AlwaysThinking <- (thinkingType = "only")
                    descriptor.ModelInfo <- ModelInfo(providerId, id, displayName, id, capabilities, context, Nullable<int64>(), efforts)
                    result.Add descriptor
            return result.ToArray() :> IReadOnlyList<ProviderModelDescriptor>
    }

    interface IProviderAuthAdapter with
        member _.SupportsBrowser = false
        member _.SupportsDevice = true
        member _.CanRefresh = true
        member _.BeginBrowserLoginAsync _ = Task.FromResult(null)
        member _.ExchangeBrowserCodeAsync(_, _, _, _) = Task.FromException<ProviderAuthSession>(NotSupportedException("Kimi Code uses device authorization."))
        member _.BeginDeviceLoginAsync(ct) = task {
            use! document = sendForm false deviceEndpoint null [ "client_id", clientId ] ct
            let root = document.RootElement
            let deviceCode = KimiCodeAuthJson.stringValue root "device_code" ""
            let userCode = KimiCodeAuthJson.stringValue root "user_code" ""
            let verificationText = KimiCodeAuthJson.stringValue root "verification_uri" (KimiCodeAuthJson.stringValue root "verification_url" "")
            let completeText = KimiCodeAuthJson.stringValue root "verification_uri_complete" verificationText
            let verification = match Uri.TryCreate(verificationText, UriKind.Absolute) with true, uri when uri.Scheme = "https" -> uri | _ -> raise (AiException("Kimi Code returned an invalid device verification URL."))
            let complete = match Uri.TryCreate(completeText, UriKind.Absolute) with true, uri when uri.Scheme = "https" -> uri | _ -> raise (AiException("Kimi Code returned an invalid device verification URL."))
            if String.IsNullOrWhiteSpace deviceCode || String.IsNullOrWhiteSpace userCode then raise (AiException("Kimi Code device authorization response was incomplete."))
            let expires = KimiCodeAuthJson.integerValue root "expires_in"
            let interval = KimiCodeAuthJson.integerValue root "interval"
            if not expires.HasValue || expires.Value <= 0L then raise (AiException("Kimi Code device authorization response omitted a valid expiry."))
            if interval.HasValue && interval.Value <= 0L then raise (AiException("Kimi Code device authorization response contained an invalid polling interval."))
            let expiry = expires.Value
            let polling = if interval.HasValue then interval.Value else 5L
            return ProviderDeviceAuthorization(deviceCode, userCode, verification, complete, clock.GetUtcNow().AddSeconds(float expiry), TimeSpan.FromSeconds(float polling))
        }
        member _.PollDeviceLoginAsync(device, accountId, ct) = task {
            if isNull device || clock.GetUtcNow() >= device.ExpiresAt then return Expired
            else
                use! document = sendForm true tokenEndpoint deviceId [ "client_id", clientId; "device_code", device.DeviceCode; "grant_type", "urn:ietf:params:oauth:grant-type:device_code" ] ct
                let root = document.RootElement
                match KimiCodeAuthJson.stringValue root "error" "" with
                | "authorization_pending" -> return Pending
                | "slow_down" -> return SlowDown
                | "access_denied" -> return Denied
                | "expired_token" -> return Expired
                | "" -> return Authorized(makeSession accountId root)
                | _ -> return raise (AiException($"Kimi Code device authorization failed ({KimiCodeAuthJson.oauthError root})."))
        }
        member _.RefreshAsync(session, ct) = task {
            if isNull session || String.IsNullOrWhiteSpace session.RefreshToken then raise (AiException("Kimi Code session has no refresh token. Sign in again."))
            use! document = sendForm false tokenEndpoint session.DeviceId [ "client_id", clientId; "refresh_token", session.RefreshToken; "grant_type", "refresh_token" ] ct
            let refreshed = makeSession session.AccountId document.RootElement
            if String.IsNullOrWhiteSpace refreshed.RefreshToken then refreshed.RefreshToken <- session.RefreshToken
            if not (String.IsNullOrWhiteSpace session.DeviceId) then refreshed.DeviceId <- session.DeviceId
            return refreshed
        }
        member _.DiscoverModelsAsync(session, ct) = discover session ct
