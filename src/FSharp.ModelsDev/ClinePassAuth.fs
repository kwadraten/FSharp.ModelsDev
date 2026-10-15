namespace FSharp.ModelsDev.Providers

open System
open System.Collections.Generic
open System.Globalization
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog

module private ClinePassAuthJson =
    let tryProperty (element: JsonElement) (name: string) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then Some value else None

    let stringValue (element: JsonElement) (name: string) (fallback: string) =
        match tryProperty element name with
        | Some value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ -> fallback

    let int64Value (element: JsonElement) (name: string) =
        match tryProperty element name with
        | Some value when value.ValueKind = JsonValueKind.Number -> match value.TryGetInt64() with true, n -> Nullable n | _ -> Nullable()
        | _ -> Nullable()

    let boolValue (element: JsonElement) (name: string) =
        match tryProperty element name with
        | Some value when value.ValueKind = JsonValueKind.True -> Some true
        | Some value when value.ValueKind = JsonValueKind.False -> Some false
        | _ -> None

    let strings (element: JsonElement) (name: string) =
        match tryProperty element name with
        | Some value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray() |> Seq.choose (fun item -> if item.ValueKind = JsonValueKind.String then Some(item.GetString()) else None) |> Seq.toArray
        | _ -> Array.empty

    let safeErrorCode (root: JsonElement) =
        match stringValue root "error" "" with
        | "authorization_pending" | "slow_down" | "access_denied" | "expired_token" | "invalid_grant" -> stringValue root "error" ""
        | _ -> "authentication_failed"

module private ClinePassToken =
    let prefix (token: string) =
        if String.IsNullOrWhiteSpace token then raise (AiException("ClinePass returned an empty access token."))
        if token.StartsWith("workos:", StringComparison.Ordinal) then token else "workos:" + token

    let jwtExpiry (token: string) =
        try
            let parts = token.Split('.')
            if parts.Length < 2 then Nullable<DateTimeOffset>()
            else
                let payload = parts[1].Replace('-', '+').Replace('_', '/')
                let padded = payload + String('=', (4 - payload.Length % 4) % 4)
                use document = JsonDocument.Parse(Convert.FromBase64String(padded))
                match ClinePassAuthJson.int64Value document.RootElement "exp" with
                | value when value.HasValue -> Nullable(DateTimeOffset.FromUnixTimeSeconds(value.Value))
                | _ -> Nullable<DateTimeOffset>()
        with _ -> Nullable<DateTimeOffset>()

    let expiration (now: DateTimeOffset) (root: JsonElement) (accessToken: string) : Nullable<DateTimeOffset> =
        let rawExpiry = ClinePassAuthJson.stringValue root "expiresAt" (ClinePassAuthJson.stringValue root "expires_at" "")
        match DateTimeOffset.TryParse(rawExpiry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
        | true, exact -> Nullable exact
        | _ ->
            match ClinePassAuthJson.int64Value root "expires_in" with
            | value when value.HasValue && value.Value > 0L -> Nullable(now.AddSeconds(float value.Value))
            | _ -> jwtExpiry accessToken

/// ClinePass OAuth adapter: WorkOS device login, Cline token registration/refresh, and authenticated catalog discovery.
[<Sealed>]
type internal ClinePassAuthAdapter(options: ProviderAuthOptions, injectedHttp: HttpClient, injectedClock: TimeProvider) =
    let options = if isNull options then ProviderAuthOptions.ClinePass() else options
    let http = if isNull injectedHttp then new HttpClient() else injectedHttp
    let clock = if isNull injectedClock then TimeProvider.System else injectedClock
    let providerId = options.ProviderId
    let authority = options.Authority
    let clientId = if String.IsNullOrWhiteSpace options.ClientId then "client_01K3A541FN8TA3EPPHTD2325AR" else options.ClientId
    let deviceEndpoint = if String.IsNullOrWhiteSpace options.DeviceAuthorizationEndpoint then "https://api.workos.com/user_management/authorize/device" else options.DeviceAuthorizationEndpoint
    let tokenEndpoint = if String.IsNullOrWhiteSpace options.TokenEndpoint then "https://api.workos.com/user_management/authenticate" else options.TokenEndpoint
    let apiBase = if String.IsNullOrWhiteSpace options.ApiBaseUrl then "https://api.cline.bot/api/v1" else options.ApiBaseUrl.TrimEnd('/')
    let origin (uri: Uri) = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/').ToLowerInvariant()
    let trustedOrigins =
        seq {
            yield "https://api.workos.com"
            yield "https://api.cline.bot"
            if not (isNull options.TrustedOrigins) then yield! options.TrustedOrigins
        }
        |> Seq.choose (fun value -> match Uri.TryCreate(value, UriKind.Absolute) with true, uri when uri.Scheme = "https" -> Some(origin uri) | _ -> None)
        |> fun origins -> HashSet<string>(origins, StringComparer.OrdinalIgnoreCase)
    let requireTrusted (label: string) (value: string) =
        match Uri.TryCreate(value, UriKind.Absolute) with
        | true, uri when uri.Scheme = "https" && trustedOrigins.Contains(origin uri) -> uri
        | _ -> raise (AiException($"ClinePass {label} endpoint must use HTTPS and a trusted origin."))

    let parseJson (body: string) (status: int) =
        try JsonDocument.Parse(body)
        with _ -> raise (AiException($"ClinePass returned invalid JSON (HTTP {status}).", status))

    let postForm (endpoint: string) (values: (string * string) list) (ct: CancellationToken) = task {
        let endpoint = requireTrusted "WorkOS" endpoint
        use request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        request.Content <- new FormUrlEncodedContent(values |> Seq.map (fun (key, value) -> KeyValuePair<string,string>(key, value)))
        use! response = http.SendAsync(request, ct)
        let! body = response.Content.ReadAsStringAsync(ct)
        let document = parseJson body (int response.StatusCode)
        return int response.StatusCode, document
    }

    let postJson (endpoint: string) (userAgent: string) (payload: JsonObject) (ct: CancellationToken) = task {
        let endpoint = requireTrusted "Cline API" endpoint
        use request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        request.Content <- new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        if not (String.IsNullOrWhiteSpace userAgent) then request.Headers.TryAddWithoutValidation("User-Agent", userAgent) |> ignore
        use! response = http.SendAsync(request, ct)
        let! body = response.Content.ReadAsStringAsync(ct)
        let document = parseJson body (int response.StatusCode)
        if not response.IsSuccessStatusCode then
            let code = ClinePassAuthJson.safeErrorCode document.RootElement
            document.Dispose()
            if code = "invalid_grant" then raise (AiException("ClinePass token service rejected the refresh token (invalid_grant).", int response.StatusCode))
            else raise (AiException($"ClinePass token service failed with HTTP {int response.StatusCode}.", int response.StatusCode))
        return document
    }

    let makeSession (accountId: string) (accessToken: string) (refreshToken: string) (expiresAt: Nullable<DateTimeOffset>) =
        let session = ProviderAuthSession(providerId, authority, accountId)
        session.CredentialKind <- ProviderCredentialKind.OAuth
        session.AccessToken <- ClinePassToken.prefix accessToken
        session.RefreshToken <- refreshToken
        session.ApiBaseUrl <- apiBase
        if expiresAt.HasValue then session.ExpiresAt <- expiresAt
        session

    let registerTokens (accountId: string) (rawAccessToken: string) (rawRefreshToken: string) (ct: CancellationToken) = task {
        let payload = JsonObject()
        payload["accessToken"] <- JsonValue.Create(rawAccessToken)
        payload["refreshToken"] <- JsonValue.Create(rawRefreshToken)
        use! document = postJson (apiBase + "/auth/register") "FSharp.ModelsDev" payload ct
        let root = document.RootElement
        let success = ClinePassAuthJson.boolValue root "success" = Some true
        let data = match ClinePassAuthJson.tryProperty root "data" with Some value when value.ValueKind = JsonValueKind.Object -> value | _ -> Unchecked.defaultof<JsonElement>
        let access = ClinePassAuthJson.stringValue data "accessToken" ""
        let refresh = ClinePassAuthJson.stringValue data "refreshToken" rawRefreshToken
        if not success || String.IsNullOrWhiteSpace access then raise (AiException("ClinePass token registration response was incomplete."))
        let expiry = ClinePassToken.expiration (clock.GetUtcNow()) data access
        return makeSession accountId access refresh expiry
    }

    let discoverModels (session: ProviderAuthSession) (ct: CancellationToken) = task {
        if isNull session || String.IsNullOrWhiteSpace session.AccessToken then raise (AiException("ClinePass model discovery requires an authenticated session."))
        let baseUrl = if String.IsNullOrWhiteSpace session.ApiBaseUrl then apiBase else session.ApiBaseUrl.TrimEnd('/')
        let baseUri = requireTrusted "API base URL" baseUrl
        use request = new HttpRequestMessage(HttpMethod.Get, Uri(baseUri, baseUri.AbsolutePath.TrimEnd('/') + "/models"))
        request.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", session.AccessToken)
        request.Headers.Accept.ParseAdd("application/json")
        use! response = http.SendAsync(request, ct)
        let! body = response.Content.ReadAsStringAsync(ct)
        if not response.IsSuccessStatusCode then raise (AiException($"ClinePass model discovery failed with HTTP {int response.StatusCode}.", int response.StatusCode))
        use document = parseJson body (int response.StatusCode)
        let models =
            match ClinePassAuthJson.tryProperty document.RootElement "data", ClinePassAuthJson.tryProperty document.RootElement "models" with
            | Some data, _ when data.ValueKind = JsonValueKind.Array -> data
            | _, Some list when list.ValueKind = JsonValueKind.Array -> list
            | _ -> raise (AiException("ClinePass model discovery response did not contain a models array."))
        let results = ResizeArray<ProviderModelDescriptor>()
        for item in models.EnumerateArray() do
            if item.ValueKind <> JsonValueKind.Object then raise (AiException("ClinePass model discovery contained a malformed model entry."))
            let id = ClinePassAuthJson.stringValue item "id" (ClinePassAuthJson.stringValue item "model" "")
            if String.IsNullOrWhiteSpace id then raise (AiException("ClinePass model discovery contained an entry without a model id."))
            let displayName = ClinePassAuthJson.stringValue item "display_name" (ClinePassAuthJson.stringValue item "name" id)
            let context =
                let first = ClinePassAuthJson.int64Value item "context_length"
                if first.HasValue then first else ClinePassAuthJson.int64Value item "contextLength"
            let supportedParams = ClinePassAuthJson.strings item "supported_parameters"
            let effortProperty = ClinePassAuthJson.strings item "reasoning_efforts"
            let effortSupported =
                ClinePassAuthJson.boolValue item "supports_reasoning" = Some true
                || (supportedParams |> Array.exists (fun value -> value.Contains("reason", StringComparison.OrdinalIgnoreCase) || value.Contains("thinking", StringComparison.OrdinalIgnoreCase)))
            let efforts = if effortSupported then effortProperty else Array.empty
            let mutable capabilities = ModelCapabilities.TextInput
            if ClinePassAuthJson.boolValue item "supports_tool_use" = Some true || (supportedParams |> Array.exists (fun value -> value.Contains("tool", StringComparison.OrdinalIgnoreCase))) then capabilities <- capabilities ||| ModelCapabilities.ToolCalling
            if ClinePassAuthJson.boolValue item "supports_image_in" = Some true then capabilities <- capabilities ||| ModelCapabilities.ImageInput
            if effortSupported then capabilities <- capabilities ||| ModelCapabilities.Reasoning
            let descriptor = ProviderModelDescriptor(providerId, id, "openai-chat", baseUrl)
            descriptor.AccountId <- session.AccountId
            descriptor.Authority <- authority
            descriptor.SupportedEndpoints <- [| "chat.completions" |]
            descriptor.ReasoningEfforts <- efforts
            descriptor.ModelInfo <- ModelInfo(providerId, id, displayName, id, capabilities, context, Nullable<int64>(), efforts)
            results.Add descriptor
        return results.ToArray() :> IReadOnlyList<ProviderModelDescriptor>
    }

    interface IProviderAuthAdapter with
        member _.SupportsBrowser = false
        member _.SupportsDevice = true
        member _.CanRefresh = true
        member _.BeginBrowserLoginAsync _ = Task.FromResult(null)
        member _.ExchangeBrowserCodeAsync(_, _, _, _) = Task.FromException<ProviderAuthSession>(NotSupportedException("ClinePass uses WorkOS device authorization."))
        member _.BeginDeviceLoginAsync(ct) = task {
            let! status, document = postForm deviceEndpoint [ "client_id", clientId ] ct
            use document = document
            if status < 200 || status >= 300 then raise (AiException($"ClinePass device authorization failed with HTTP {status}.", status))
            let root = document.RootElement
            let deviceCode = ClinePassAuthJson.stringValue root "device_code" ""
            let userCode = ClinePassAuthJson.stringValue root "user_code" ""
            let verificationText = ClinePassAuthJson.stringValue root "verification_uri" ""
            let completeText = ClinePassAuthJson.stringValue root "verification_uri_complete" verificationText
            let verification = match Uri.TryCreate(verificationText, UriKind.Absolute) with true, uri when uri.Scheme = "https" -> uri | _ -> raise (AiException("WorkOS returned an invalid device verification URL."))
            let complete = match Uri.TryCreate(completeText, UriKind.Absolute) with true, uri when uri.Scheme = "https" -> uri | _ -> raise (AiException("WorkOS returned an invalid device verification URL."))
            if String.IsNullOrWhiteSpace deviceCode || String.IsNullOrWhiteSpace userCode then raise (AiException("WorkOS device authorization response was incomplete."))
            let expires = ClinePassAuthJson.int64Value root "expires_in"
            let interval = ClinePassAuthJson.int64Value root "interval"
            if not expires.HasValue || expires.Value <= 0L then raise (AiException("WorkOS device authorization returned an invalid expiry."))
            if interval.HasValue && interval.Value <= 0L then raise (AiException("WorkOS device authorization returned an invalid polling interval."))
            let expiry = expires.Value
            let polling = if interval.HasValue then interval.Value else 5L
            return ProviderDeviceAuthorization(deviceCode, userCode, verification, complete, clock.GetUtcNow().AddSeconds(float expiry), TimeSpan.FromSeconds(float polling))
        }
        member _.PollDeviceLoginAsync(device, accountId, ct) = task {
            if isNull device || clock.GetUtcNow() >= device.ExpiresAt then return Expired
            else
                let! status, document = postForm tokenEndpoint [ "grant_type", "urn:ietf:params:oauth:grant-type:device_code"; "device_code", device.DeviceCode; "client_id", clientId ] ct
                use document = document
                let root = document.RootElement
                match ClinePassAuthJson.safeErrorCode root with
                | "authorization_pending" -> return Pending
                | "slow_down" -> return SlowDown
                | "access_denied" -> return Denied
                | "expired_token" -> return Expired
                | _ when status >= 200 && status < 300 ->
                    let rawAccess = ClinePassAuthJson.stringValue root "access_token" ""
                    let rawRefresh = ClinePassAuthJson.stringValue root "refresh_token" ""
                    if String.IsNullOrWhiteSpace rawAccess || String.IsNullOrWhiteSpace rawRefresh then
                        raise (AiException("WorkOS device token response was incomplete."))
                    let! session = registerTokens accountId rawAccess rawRefresh ct
                    return Authorized session
                | _ -> return raise (AiException($"WorkOS device authorization failed ({ClinePassAuthJson.safeErrorCode root}).", status))
        }
        member _.RefreshAsync(session, ct) = task {
            if isNull session || String.IsNullOrWhiteSpace session.RefreshToken then raise (AiException("ClinePass session has no refresh token. Sign in again."))
            let payload = JsonObject()
            payload["granttype"] <- JsonValue.Create("refresh_token")
            payload["refreshToken"] <- JsonValue.Create(session.RefreshToken)
            use! document = postJson (apiBase + "/auth/refresh") null payload ct
            let root = document.RootElement
            let data = match ClinePassAuthJson.tryProperty root "data" with Some value when value.ValueKind = JsonValueKind.Object -> value | _ -> root
            let access = ClinePassAuthJson.stringValue data "accessToken" ""
            let refresh = ClinePassAuthJson.stringValue data "refreshToken" session.RefreshToken
            if String.IsNullOrWhiteSpace access || String.IsNullOrWhiteSpace refresh then raise (AiException("ClinePass token refresh response was incomplete."))
            let expiry = ClinePassToken.expiration (clock.GetUtcNow()) data access
            let updated = makeSession session.AccountId access refresh expiry
            if not (String.IsNullOrWhiteSpace session.DeviceId) then updated.DeviceId <- session.DeviceId
            return updated
        }
        member _.DiscoverModelsAsync(session, ct) = discoverModels session ct
