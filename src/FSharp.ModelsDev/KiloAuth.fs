namespace FSharp.ModelsDev.Providers

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog

module private KiloAuthHelpers =
    let str (e: JsonElement) (name: string) (fallback: string) =
        let mutable v = Unchecked.defaultof<JsonElement>
        if e.ValueKind = JsonValueKind.Object && e.TryGetProperty(name, &v) && v.ValueKind = JsonValueKind.String then v.GetString() else fallback
    let int64 (e: JsonElement) (name: string) =
        let mutable v = Unchecked.defaultof<JsonElement>
        if e.ValueKind = JsonValueKind.Object && e.TryGetProperty(name, &v) then match v.TryGetInt64() with true, n -> Nullable n | _ -> Nullable() else Nullable()
    let strings (e: JsonElement) (name: string) =
        let mutable v = Unchecked.defaultof<JsonElement>
        if e.ValueKind = JsonValueKind.Object && e.TryGetProperty(name, &v) && v.ValueKind = JsonValueKind.Array then
            v.EnumerateArray() |> Seq.choose (fun x -> if x.ValueKind = JsonValueKind.String then Some(x.GetString()) else None) |> Seq.toArray
        else Array.empty
    let safeOrigin (value: string) =
        match Uri.TryCreate(value, UriKind.Absolute) with
        | true, uri when uri.Scheme = "https" -> Some(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/'))
        | _ -> None
    let jwtExpiry (token: string) =
        try
            let parts = token.Split('.')
            if parts.Length < 2 then Nullable()
            else
                let payload = parts[1].Replace('-', '+').Replace('_', '/')
                let padded = payload + String('=', (4 - payload.Length % 4) % 4)
                use doc = JsonDocument.Parse(Convert.FromBase64String(padded))
                let mutable exp = Unchecked.defaultof<JsonElement>
                if doc.RootElement.TryGetProperty("exp", &exp) then
                    match exp.TryGetInt64() with true, seconds -> Nullable(DateTimeOffset.FromUnixTimeSeconds(seconds)) | _ -> Nullable()
                else Nullable()
        with _ -> Nullable()

[<Sealed>]
type internal KiloAuthAdapter(options: ProviderAuthOptions, http: HttpClient, clock: TimeProvider) =
    let options = if isNull options then ProviderAuthOptions.Kilo() else options
    let http = if isNull http then new HttpClient() else http
    let clock = if isNull clock then TimeProvider.System else clock
    let providerId = "kilo"
    let authority = options.Authority
    let issuer = if String.IsNullOrWhiteSpace options.Issuer then "https://api.kilo.ai" else options.Issuer.TrimEnd('/')
    let deviceEndpoint = if String.IsNullOrWhiteSpace options.DeviceAuthorizationEndpoint then issuer + "/api/device-auth/codes" else options.DeviceAuthorizationEndpoint
    let apiBase = if String.IsNullOrWhiteSpace options.ApiBaseUrl then "https://api.kilo.ai/api/gateway" else options.ApiBaseUrl.TrimEnd('/')
    let trustedOrigins =
        seq { yield issuer; yield apiBase; yield! options.TrustedOrigins }
        |> Seq.choose KiloAuthHelpers.safeOrigin
        |> Seq.toArray
        |> fun origins -> HashSet<string>(origins, StringComparer.OrdinalIgnoreCase)
    let trustedEndpoint label value =
        match Uri.TryCreate(value, UriKind.Absolute) with
        | true, uri when uri.Scheme = "https" && trustedOrigins.Contains(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/')) -> uri
        | _ -> raise (AiException($"Kilo {label} endpoint is outside the configured trusted origins."))
    let parseJson (response: HttpResponseMessage) (body: string) =
        try JsonDocument.Parse(body)
        with _ -> raise (AiException($"Kilo authentication request returned invalid JSON (HTTP {int response.StatusCode})."))
    let makeSession accountId token =
        if String.IsNullOrWhiteSpace token then raise (AiException("Kilo authorization response did not contain an access token."))
        let session = ProviderAuthSession(providerId, authority, accountId)
        session.CredentialKind <- ProviderCredentialKind.OAuth
        session.AccessToken <- token
        session.ApiBaseUrl <- apiBase
        session.OrganizationId <- options.OrganizationId
        let expiry = KiloAuthHelpers.jwtExpiry token
        if expiry.HasValue then session.ExpiresAt <- expiry
        session
    interface IProviderAuthAdapter with
        member _.SupportsBrowser = false
        member _.SupportsDevice = true
        member _.CanRefresh = false
        member _.BeginBrowserLoginAsync(_ct) = Task.FromException<ProviderBrowserLogin>(AiException("Kilo authentication uses device authorization."))
        member _.ExchangeBrowserCodeAsync(_login, _code, _accountId, _ct) = Task.FromException<ProviderAuthSession>(AiException("Kilo authentication uses device authorization."))
        member _.BeginDeviceLoginAsync(ct) = task {
            let endpoint = trustedEndpoint "device authorization" deviceEndpoint
            use req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            req.Content <- new StringContent("{}", Encoding.UTF8, "application/json")
            use! response = http.SendAsync(req, ct)
            if not response.IsSuccessStatusCode then
                let! _ = response.Content.ReadAsStringAsync(ct)
                raise (AiException($"Kilo device authorization request failed with HTTP {int response.StatusCode}.", int response.StatusCode))
            let! body = response.Content.ReadAsStringAsync(ct)
            use doc = parseJson response body
            let root = doc.RootElement
            let code = KiloAuthHelpers.str root "code" ""
            let verification = KiloAuthHelpers.str root "verificationUrl" ""
            let expires = KiloAuthHelpers.int64 root "expiresIn"
            if String.IsNullOrWhiteSpace code then raise (AiException("Kilo device authorization response did not contain a code."))
            let verificationUri =
                match Uri.TryCreate(verification, UriKind.Absolute) with
                | true, uri when uri.Scheme = "https" -> uri
                | true, uri when uri.Scheme = "http" && (uri.Host = "localhost" || uri.Host = "127.0.0.1") -> uri
                | _ -> raise (AiException("Kilo returned an unsafe device verification URL."))
            let seconds = if expires.HasValue then Math.Clamp(expires.Value, 1L, 86400L) else 600L
            return ProviderDeviceAuthorization(code, code, verificationUri, verificationUri, clock.GetUtcNow().AddSeconds(float seconds), TimeSpan.FromSeconds(3.))
        }
        member _.PollDeviceLoginAsync(device, accountId, ct) = task {
            if isNull device || clock.GetUtcNow() >= device.ExpiresAt then return Expired
            else
                ProviderAuthRuntime.validateAccount accountId
                let baseUri = trustedEndpoint "device poll" deviceEndpoint
                let pollUrl = baseUri.AbsoluteUri.TrimEnd('/') + "/" + Uri.EscapeDataString(device.DeviceCode)
                use! response = http.GetAsync(pollUrl, ct)
                match int response.StatusCode with
                | 202 -> return Pending
                | 403 -> return Denied
                | 410 -> return Expired
                | _ when not response.IsSuccessStatusCode ->
                    let! _ = response.Content.ReadAsStringAsync(ct)
                    return raise (AiException($"Kilo device authorization poll failed with HTTP {int response.StatusCode}.", int response.StatusCode))
                | _ ->
                    let! body = response.Content.ReadAsStringAsync(ct)
                    use doc = parseJson response body
                    let root = doc.RootElement
                    let status = KiloAuthHelpers.str root "status" ""
                    match status with
                    | "pending" -> return Pending
                    | "denied" -> return Denied
                    | "expired" -> return Expired
                    | "approved" ->
                        let token = KiloAuthHelpers.str root "token" ""
                        if String.IsNullOrWhiteSpace token then raise (AiException("Kilo approval response did not contain an access token."))
                        return Authorized(makeSession accountId token)
                    | _ -> return raise (AiException("Kilo device authorization response had an unknown status."))
        }
        member _.RefreshAsync(_session, _ct) = Task.FromException<ProviderAuthSession>(AiException("Kilo credentials cannot be refreshed. Sign in again."))
        member _.DiscoverModelsAsync(session, ct) = task {
            ProviderAuthRuntime.validateSession providerId authority (if isNull session then null else session.AccountId) session
            if String.IsNullOrWhiteSpace session.AccessToken then raise (AiException("Kilo session does not contain an access token."))
            let baseUrl = if String.IsNullOrWhiteSpace session.ApiBaseUrl then apiBase else session.ApiBaseUrl.TrimEnd('/')
            let endpoint = trustedEndpoint "gateway API" baseUrl
            use req = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/models")
            req.Headers.Authorization <- System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken)
            req.Headers.TryAddWithoutValidation("X-KILOCODE-EDITORNAME", "FSharp.ModelsDev") |> ignore
            if not (String.IsNullOrWhiteSpace session.OrganizationId) then
                req.Headers.TryAddWithoutValidation("X-KILOCODE-ORGANIZATIONID", session.OrganizationId) |> ignore
            use! response = http.SendAsync(req, ct)
            if not response.IsSuccessStatusCode then
                let! _ = response.Content.ReadAsStringAsync(ct)
                raise (AiException($"Kilo model discovery failed with HTTP {int response.StatusCode}.", int response.StatusCode))
            let! body = response.Content.ReadAsStringAsync(ct)
            use doc =
                try JsonDocument.Parse(body)
                with _ -> raise (AiException("Kilo model discovery returned invalid JSON."))
            let root = doc.RootElement
            let mutable data = Unchecked.defaultof<JsonElement>
            let entries = if root.ValueKind = JsonValueKind.Array then root else if root.TryGetProperty("data", &data) && data.ValueKind = JsonValueKind.Array then data else Unchecked.defaultof<JsonElement>
            if entries.ValueKind <> JsonValueKind.Array then return raise (AiException("Kilo model discovery returned an invalid response envelope."))
            else
                let results = ResizeArray<ProviderModelDescriptor>()
                for model in entries.EnumerateArray() do
                    let id = KiloAuthHelpers.str model "id" ""
                    if not (String.IsNullOrWhiteSpace id) then
                        let mutable architecture = Unchecked.defaultof<JsonElement>
                        let hasArchitecture = model.TryGetProperty("architecture", &architecture)
                        let inputModalities = if hasArchitecture then KiloAuthHelpers.strings architecture "input_modalities" else KiloAuthHelpers.strings model "input_modalities"
                        let outputModalities = if hasArchitecture then KiloAuthHelpers.strings architecture "output_modalities" else KiloAuthHelpers.strings model "output_modalities"
                        let endpoints = KiloAuthHelpers.strings model "supported_endpoints"
                        let parameters = KiloAuthHelpers.strings model "supported_parameters"
                        let mutable caps = ModelCapabilities.TextInput
                        if inputModalities |> Array.exists (fun x -> x.Equals("image", StringComparison.OrdinalIgnoreCase)) then caps <- caps ||| ModelCapabilities.ImageInput
                        if inputModalities |> Array.exists (fun x -> x.Contains("audio", StringComparison.OrdinalIgnoreCase)) || outputModalities |> Array.exists (fun x -> x.Contains("audio", StringComparison.OrdinalIgnoreCase)) then caps <- caps ||| ModelCapabilities.AudioInput
                        if parameters |> Array.exists (fun x -> x.Contains("tool", StringComparison.OrdinalIgnoreCase)) then caps <- caps ||| ModelCapabilities.ToolCalling
                        if parameters |> Array.exists (fun x -> x.Contains("reason", StringComparison.OrdinalIgnoreCase) || x.Contains("thinking", StringComparison.OrdinalIgnoreCase)) then caps <- caps ||| ModelCapabilities.Reasoning
                        if parameters |> Array.exists (fun x -> x.Contains("json", StringComparison.OrdinalIgnoreCase) || x.Contains("structured", StringComparison.OrdinalIgnoreCase)) then caps <- caps ||| ModelCapabilities.StructuredOutput
                        let context = KiloAuthHelpers.int64 model "context_length"
                        let maximum = KiloAuthHelpers.int64 model "max_output_tokens"
                        let displayName = KiloAuthHelpers.str model "name" id
                        let protocol = if endpoints |> Array.exists (fun x -> x.Contains("responses", StringComparison.OrdinalIgnoreCase)) then "openai-responses" else "openai-chat"
                        let descriptor = ProviderModelDescriptor(providerId, id, protocol, baseUrl)
                        descriptor.AccountId <- session.AccountId
                        descriptor.Authority <- authority
                        descriptor.ModelInfo <- ModelInfo(providerId, id, displayName, id, caps, context, maximum, [||])
                        descriptor.SupportedEndpoints <- endpoints
                        results.Add descriptor
                return results.ToArray() :> IReadOnlyList<ProviderModelDescriptor>
        }
