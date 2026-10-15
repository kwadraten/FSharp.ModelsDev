namespace FSharp.ModelsDev.Providers

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog

module private GrokAuthHelpers =
    let str (e: JsonElement) (name: string) (fallback: string) =
        let mutable v = Unchecked.defaultof<JsonElement>
        if e.ValueKind = JsonValueKind.Object && e.TryGetProperty(name, &v) && v.ValueKind = JsonValueKind.String then v.GetString() else fallback
    let int64 (e: JsonElement) (name: string) =
        let mutable v = Unchecked.defaultof<JsonElement>
        if e.ValueKind = JsonValueKind.Object && e.TryGetProperty(name, &v) then match v.TryGetInt64() with true, n -> Nullable n | _ -> Nullable() else Nullable()
    let arrayStrings (e: JsonElement) (name: string) =
        let mutable v = Unchecked.defaultof<JsonElement>
        if e.ValueKind = JsonValueKind.Object && e.TryGetProperty(name, &v) && v.ValueKind = JsonValueKind.Array then
            v.EnumerateArray() |> Seq.choose (fun x -> if x.ValueKind = JsonValueKind.String then Some(x.GetString()) else None) |> Seq.toArray
        else Array.empty
    let b64url (bytes: byte[]) = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
    let randomToken bytes = RandomNumberGenerator.GetBytes(bytes) |> b64url
    let challenge (verifier: string) = SHA256.HashData(Encoding.ASCII.GetBytes verifier) |> b64url
    let form (values: (string*string) seq) = new FormUrlEncodedContent(values |> Seq.map (fun (k,v) -> KeyValuePair<string,string>(k,v)))
    let safeError code =
        match code with
        | "authorization_pending" | "slow_down" | "access_denied" | "expired_token" | "invalid_grant" -> code
        | _ -> "authentication_failed"
    let origin (uri: Uri) = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/').ToLowerInvariant()

[<Sealed>]
type internal GrokAuthAdapter(options: ProviderAuthOptions, http: HttpClient, clock: TimeProvider) =
    let options = if isNull options then ProviderAuthOptions.Grok() else options
    let http = if isNull http then new HttpClient() else http
    let clock = if isNull clock then TimeProvider.System else clock
    let providerId = "grok"
    let authority = options.Authority
    let issuer = if String.IsNullOrWhiteSpace options.Issuer then "https://auth.x.ai" else options.Issuer.TrimEnd('/')
    let trustedOrigins =
        seq { yield "https://auth.x.ai"; yield "https://cli-chat-proxy.grok.com"; yield! options.TrustedOrigins }
        |> Seq.choose (fun s -> match Uri.TryCreate(s, UriKind.Absolute) with true, u when u.Scheme = "https" -> Some(GrokAuthHelpers.origin u) | _ -> None)
        |> Seq.toArray
        |> fun origins -> HashSet<string>(origins, StringComparer.OrdinalIgnoreCase)
    let requireTrustedEndpoint label (value: string) =
        match Uri.TryCreate(value, UriKind.Absolute) with
        | true, u when u.Scheme = "https" && trustedOrigins.Contains(GrokAuthHelpers.origin u) -> u
        | _ -> raise (AiException($"Grok {label} endpoint is outside the configured trusted origins."))
    let authEndpoint = if String.IsNullOrWhiteSpace options.AuthorizationEndpoint then issuer + "/oauth2/authorize" else options.AuthorizationEndpoint
    let tokenEndpoint = if String.IsNullOrWhiteSpace options.TokenEndpoint then issuer + "/oauth2/token" else options.TokenEndpoint
    let deviceEndpoint = if String.IsNullOrWhiteSpace options.DeviceAuthorizationEndpoint then issuer + "/oauth2/device/code" else options.DeviceAuthorizationEndpoint
    let apiBase = if String.IsNullOrWhiteSpace options.ApiBaseUrl then "https://cli-chat-proxy.grok.com/v1" else options.ApiBaseUrl.TrimEnd('/')
    let scopes = if isNull options.Scopes || options.Scopes.Count = 0 then [| "openid"; "profile"; "email"; "offline_access"; "grok-cli:access"; "api:access" |] else options.Scopes |> Seq.toArray
    let readJson (response: HttpResponseMessage) (ct: CancellationToken) = task {
        if not response.IsSuccessStatusCode then
            let! body = response.Content.ReadAsStringAsync(ct)
            let safeCode =
                try
                    use errorDoc = JsonDocument.Parse(body)
                    GrokAuthHelpers.str errorDoc.RootElement "error" ""
                with _ -> ""
            let code = GrokAuthHelpers.safeError safeCode
            if code <> "authentication_failed" then raise (AiException($"Grok authentication failed ({code}).", int response.StatusCode))
            else raise (AiException($"Grok authentication request failed with HTTP {int response.StatusCode}.", int response.StatusCode))
        let! body = response.Content.ReadAsStringAsync(ct)
        try return JsonDocument.Parse(body)
        with _ -> return raise (AiException("Grok returned an invalid authentication response."))
    }
    let postForm (endpoint: string) (values: seq<string * string>) (ct: CancellationToken) = task {
        let uri = requireTrustedEndpoint "OAuth" endpoint
        use req = new HttpRequestMessage(HttpMethod.Post, uri)
        req.Content <- GrokAuthHelpers.form values
        use! response = http.SendAsync(req, ct)
        return! readJson response ct
    }
    let validateDiscovery (ct: CancellationToken) = task {
        let uri = requireTrustedEndpoint "issuer" (issuer + "/.well-known/openid-configuration")
        use! response = http.GetAsync(uri, ct)
        use! doc = readJson response ct
        let root = doc.RootElement
        let discoveredIssuer = GrokAuthHelpers.str root "issuer" ""
        if not (String.Equals(discoveredIssuer.TrimEnd('/'), issuer, StringComparison.OrdinalIgnoreCase)) then
            raise (AiException("Grok OIDC discovery issuer did not match the configured issuer."))
        let discoveredToken = GrokAuthHelpers.str root "token_endpoint" ""
        let discoveredAuth = GrokAuthHelpers.str root "authorization_endpoint" ""
        let discoveredDevice = GrokAuthHelpers.str root "device_authorization_endpoint" ""
        for label, discovered, configured in [ "token" ,discoveredToken, tokenEndpoint; "authorization", discoveredAuth, authEndpoint ] do
            if String.IsNullOrWhiteSpace discovered then raise (AiException($"Grok OIDC discovery omitted the {label} endpoint."))
            let found = requireTrustedEndpoint label discovered
            let expected = requireTrustedEndpoint label configured
            if found <> expected then raise (AiException($"Grok discovered {label} endpoint did not match the configured endpoint."))
        if not (String.IsNullOrWhiteSpace discoveredDevice) then
            let found = requireTrustedEndpoint "device authorization" discoveredDevice
            let expected = requireTrustedEndpoint "device authorization" deviceEndpoint
            if found <> expected then raise (AiException("Grok discovered device authorization endpoint did not match the configured endpoint."))
    }
    let makeSession accountId (root: JsonElement) =
        let access = GrokAuthHelpers.str root "access_token" ""
        if String.IsNullOrWhiteSpace access then raise (AiException("Grok token response did not contain an access token."))
        let session = ProviderAuthSession(providerId, authority, accountId)
        session.AccessToken <- access
        session.RefreshToken <- GrokAuthHelpers.str root "refresh_token" null
        session.ApiBaseUrl <- apiBase
        let expires = GrokAuthHelpers.int64 root "expires_in"
        if expires.HasValue && expires.Value > 0L then session.ExpiresAt <- Nullable(clock.GetUtcNow().AddSeconds(float expires.Value))
        session
    interface IProviderAuthAdapter with
        member _.SupportsBrowser = true
        member _.SupportsDevice = true
        member _.CanRefresh = true
        member _.BeginBrowserLoginAsync(ct) = task {
            do! validateDiscovery ct
            let state, verifier = GrokAuthHelpers.randomToken 32, GrokAuthHelpers.randomToken 48
            let challenge = GrokAuthHelpers.challenge verifier
            let uri = UriBuilder(requireTrustedEndpoint "authorization" authEndpoint)
            let query = [ "client_id", options.ClientId; "redirect_uri", options.RedirectUri; "response_type", "code"; "scope", String.Join(" ", scopes); "state", state; "code_challenge", challenge; "code_challenge_method", "S256" ]
            uri.Query <- String.Join("&", query |> Seq.map (fun (k,v) -> Uri.EscapeDataString(k) + "=" + Uri.EscapeDataString(if isNull v then "" else v)))
            return ProviderBrowserLogin(uri.Uri, state, verifier, options.RedirectUri, clock.GetUtcNow().Add(options.LoginTimeout))
        }
        member _.ExchangeBrowserCodeAsync(login, code, accountId, ct) = task {
            if isNull login then raise (AiException("Grok login state is missing."))
            if clock.GetUtcNow() > login.ExpiresAt then raise (AiException("Grok login state expired. Start sign-in again."))
            ProviderAuthRuntime.validateAccount accountId
            use! doc = postForm tokenEndpoint [ "grant_type", "authorization_code"; "client_id", (if isNull options.ClientId then "" else options.ClientId); "code", code; "redirect_uri", login.RedirectUri; "code_verifier", login.Verifier ] ct
            return makeSession accountId doc.RootElement
        }
        member _.BeginDeviceLoginAsync(ct) = task {
            do! validateDiscovery ct
            use! doc = postForm deviceEndpoint [ "client_id", (if isNull options.ClientId then "" else options.ClientId); "scope", String.Join(" ", scopes) ] ct
            let root = doc.RootElement
            let deviceCode = GrokAuthHelpers.str root "device_code" ""
            let userCode = GrokAuthHelpers.str root "user_code" ""
            let verify = GrokAuthHelpers.str root "verification_uri" (GrokAuthHelpers.str root "verification_url" "")
            let complete = GrokAuthHelpers.str root "verification_uri_complete" null
            if String.IsNullOrWhiteSpace deviceCode || String.IsNullOrWhiteSpace userCode then raise (AiException("Grok device authorization response was incomplete."))
            let verification =
                match Uri.TryCreate(verify, UriKind.Absolute) with
                | true, u when u.Scheme = "https" || (u.Scheme = "http" && (u.Host = "localhost" || u.Host = "127.0.0.1")) -> u
                | _ -> raise (AiException("Grok returned an unsafe device verification URL."))
            let completeUri =
                if String.IsNullOrWhiteSpace complete then verification
                else
                    match Uri.TryCreate(complete, UriKind.Absolute) with
                    | true, u when u.Scheme = "https" -> u
                    | _ -> raise (AiException("Grok returned an unsafe device verification URL."))
            let expires = GrokAuthHelpers.int64 root "expires_in"
            let interval = GrokAuthHelpers.int64 root "interval"
            if not expires.HasValue || expires.Value <= 0L then raise (AiException("Grok device authorization response had an invalid expiry."))
            if interval.HasValue && interval.Value <= 0L then raise (AiException("Grok device authorization response had an invalid polling interval."))
            let expirySeconds = expires.Value
            let pollSeconds = if interval.HasValue then interval.Value else 5L
            return ProviderDeviceAuthorization(deviceCode, userCode, verification, completeUri, clock.GetUtcNow().AddSeconds(float expirySeconds), TimeSpan.FromSeconds(float pollSeconds))
        }
        member _.PollDeviceLoginAsync(device, accountId, ct) = task {
            if isNull device then return Expired
            elif clock.GetUtcNow() >= device.ExpiresAt then return Expired
            else
                ProviderAuthRuntime.validateAccount accountId
                let endpoint = requireTrustedEndpoint "OAuth" tokenEndpoint
                use req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                req.Content <- GrokAuthHelpers.form [ "grant_type", "urn:ietf:params:oauth:grant-type:device_code"; "client_id", (if isNull options.ClientId then "" else options.ClientId); "device_code", device.DeviceCode ]
                use! response = http.SendAsync(req, ct)
                let! body = response.Content.ReadAsStringAsync(ct)
                let doc =
                    try JsonDocument.Parse(body)
                    with _ -> raise (AiException($"Grok device authorization failed with HTTP {int response.StatusCode}."))
                use doc = doc
                let root = doc.RootElement
                let error = GrokAuthHelpers.str root "error" ""
                match error with
                | "authorization_pending" -> return Pending
                | "slow_down" -> return SlowDown
                | "access_denied" -> return Denied
                | "expired_token" -> return Expired
                | "" when response.IsSuccessStatusCode -> return Authorized(makeSession accountId root)
                | "" -> return raise (AiException($"Grok device authorization failed with HTTP {int response.StatusCode}.", int response.StatusCode))
                | _ -> return raise (AiException($"Grok device authorization failed ({GrokAuthHelpers.safeError error})."))
        }
        member _.RefreshAsync(session, ct) = task {
            ProviderAuthRuntime.validateSession providerId authority (if isNull session then null else session.AccountId) session
            if String.IsNullOrWhiteSpace session.RefreshToken then raise (AiException("Grok session has no refresh token. Sign in again."))
            use! doc = postForm tokenEndpoint [ "grant_type", "refresh_token"; "client_id", (if isNull options.ClientId then "" else options.ClientId); "refresh_token", session.RefreshToken ] ct
            let updated = makeSession session.AccountId doc.RootElement
            if String.IsNullOrWhiteSpace updated.RefreshToken then updated.RefreshToken <- session.RefreshToken
            return updated
        }
        member _.DiscoverModelsAsync(session, ct) = task {
            ProviderAuthRuntime.validateSession providerId authority (if isNull session then null else session.AccountId) session
            let baseUrl = if String.IsNullOrWhiteSpace session.ApiBaseUrl then apiBase else session.ApiBaseUrl.TrimEnd('/')
            let baseUri = requireTrustedEndpoint "API" baseUrl
            use req = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/models")
            req.Headers.Authorization <- System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken)
            use! response = http.SendAsync(req, ct)
            use! doc = readJson response ct
            let root = doc.RootElement
            let mutable data = Unchecked.defaultof<JsonElement>
            let entries = if root.ValueKind = JsonValueKind.Array then root else if root.TryGetProperty("data", &data) && data.ValueKind = JsonValueKind.Array then data else if root.TryGetProperty("models", &data) && data.ValueKind = JsonValueKind.Array then data else Unchecked.defaultof<JsonElement>
            if entries.ValueKind <> JsonValueKind.Array then return raise (AiException("Grok model discovery returned an invalid response envelope."))
            else
                let results = ResizeArray<ProviderModelDescriptor>()
                for model in entries.EnumerateArray() do
                    let id = GrokAuthHelpers.str model "id" ""
                    if not (String.IsNullOrWhiteSpace id) then
                        let endpoints = GrokAuthHelpers.arrayStrings model "supported_endpoints"
                        let protocol = if endpoints |> Array.exists (fun e -> e.Contains("responses", StringComparison.OrdinalIgnoreCase)) then "openai-responses" else "openai-chat"
                        let mutable architecture = Unchecked.defaultof<JsonElement>
                        let hasArchitecture = model.TryGetProperty("architecture", &architecture)
                        let inputModalities = if hasArchitecture then GrokAuthHelpers.arrayStrings architecture "input_modalities" else GrokAuthHelpers.arrayStrings model "input_modalities"
                        let parameters = GrokAuthHelpers.arrayStrings model "supported_parameters"
                        let mutable caps = ModelCapabilities.TextInput
                        if inputModalities |> Array.exists (fun x -> x.Equals("image", StringComparison.OrdinalIgnoreCase)) then caps <- caps ||| ModelCapabilities.ImageInput
                        if parameters |> Array.exists (fun x -> x.Contains("tool", StringComparison.OrdinalIgnoreCase)) then caps <- caps ||| ModelCapabilities.ToolCalling
                        if parameters |> Array.exists (fun x -> x.Contains("reason", StringComparison.OrdinalIgnoreCase) || x.Contains("thinking", StringComparison.OrdinalIgnoreCase)) then caps <- caps ||| ModelCapabilities.Reasoning
                        if parameters |> Array.exists (fun x -> x.Contains("structured_output", StringComparison.OrdinalIgnoreCase) || x.Contains("json_schema", StringComparison.OrdinalIgnoreCase)) then caps <- caps ||| ModelCapabilities.StructuredOutput
                        let context = GrokAuthHelpers.int64 model "context_length"
                        let maximum = GrokAuthHelpers.int64 model "max_output_tokens"
                        let name = GrokAuthHelpers.str model "name" id
                        let descriptor = ProviderModelDescriptor(providerId, id, protocol, baseUrl)
                        descriptor.AccountId <- session.AccountId
                        descriptor.Authority <- authority
                        descriptor.SupportedEndpoints <- endpoints
                        descriptor.ModelInfo <- ModelInfo(providerId, id, name, id, caps, context, maximum, [||])
                        results.Add descriptor
                return results.ToArray() :> IReadOnlyList<ProviderModelDescriptor>
        }
