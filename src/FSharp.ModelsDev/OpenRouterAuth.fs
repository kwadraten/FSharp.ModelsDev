namespace FSharp.ModelsDev.Providers

open System
open System.Collections.Generic
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog

module private OpenRouterAuthHelpers =
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
    let base64url (bytes: byte[]) = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
    let randomToken bytes = RandomNumberGenerator.GetBytes(bytes) |> base64url
    let challenge (verifier: string) = SHA256.HashData(Encoding.ASCII.GetBytes verifier) |> base64url

[<Sealed>]
type internal OpenRouterAuthAdapter(options: ProviderAuthOptions, http: HttpClient, clock: TimeProvider) =
    let options = if isNull options then ProviderAuthOptions.OpenRouter() else options
    let http = if isNull http then new HttpClient() else http
    let clock = if isNull clock then TimeProvider.System else clock
    let providerId = "openrouter"
    let authority = options.Authority
    let apiBase = if String.IsNullOrWhiteSpace options.ApiBaseUrl then "https://openrouter.ai/api/v1" else options.ApiBaseUrl.TrimEnd('/')
    let trustedOrigins =
        seq { yield "https://openrouter.ai"; yield! options.TrustedOrigins }
        |> Seq.choose (fun s -> match Uri.TryCreate(s, UriKind.Absolute) with true, u when u.Scheme = "https" -> Some(u.GetLeftPart(UriPartial.Authority).TrimEnd('/')) | _ -> None)
        |> Seq.toArray
        |> fun origins -> HashSet<string>(origins, StringComparer.OrdinalIgnoreCase)
    let trusted label (value: string) =
        match Uri.TryCreate(value, UriKind.Absolute) with
        | true, u when u.Scheme = "https" && trustedOrigins.Contains(u.GetLeftPart(UriPartial.Authority).TrimEnd('/')) -> u
        | _ -> raise (AiException($"OpenRouter {label} endpoint is outside the configured trusted origins."))
    let tokenEndpoint = if String.IsNullOrWhiteSpace options.TokenEndpoint then "https://openrouter.ai/api/v1/auth/keys" else options.TokenEndpoint
    let readJson (response: HttpResponseMessage) (ct: CancellationToken) = task {
        if not response.IsSuccessStatusCode then
            let! _ = response.Content.ReadAsStringAsync(ct)
            raise (AiException($"OpenRouter authentication request failed with HTTP {int response.StatusCode}.", int response.StatusCode))
        let! body = response.Content.ReadAsStringAsync(ct)
        try return JsonDocument.Parse(body)
        with _ -> return raise (AiException("OpenRouter returned an invalid authentication response."))
    }
    let makeSession accountId (root: JsonElement) =
        let key = OpenRouterAuthHelpers.str root "key" ""
        if String.IsNullOrWhiteSpace key then raise (AiException("OpenRouter key exchange did not return a key."))
        let session = ProviderAuthSession(providerId, authority, accountId)
        session.CredentialKind <- ProviderCredentialKind.ApiKey
        session.AccessToken <- key
        session.ApiBaseUrl <- apiBase
        // OpenRouter's PKCE exchange returns no refresh token or documented key lifetime.
        session
    interface IProviderAuthAdapter with
        member _.SupportsBrowser = true
        member _.SupportsDevice = false
        member _.CanRefresh = false
        member _.BeginBrowserLoginAsync(ct) = task {
            ct.ThrowIfCancellationRequested()
            let state, verifier = OpenRouterAuthHelpers.randomToken 32, OpenRouterAuthHelpers.randomToken 48
            let codeChallenge = OpenRouterAuthHelpers.challenge verifier
            let authorization = if String.IsNullOrWhiteSpace options.AuthorizationEndpoint then "https://openrouter.ai/auth" else options.AuthorizationEndpoint
            let uri = UriBuilder(trusted "authorization" authorization)
            let values = ResizeArray<string*string>()
            values.Add("code_challenge", codeChallenge)
            values.Add("code_challenge_method", "S256")
            values.Add("state", state)
            if not (String.IsNullOrWhiteSpace options.RedirectUri) then values.Add("callback_url", options.RedirectUri)
            uri.Query <- String.Join("&", values |> Seq.map (fun (k,v) -> Uri.EscapeDataString(k) + "=" + Uri.EscapeDataString(v)))
            let redirect = if String.IsNullOrWhiteSpace options.RedirectUri then null else options.RedirectUri
            return ProviderBrowserLogin(uri.Uri, state, verifier, redirect, clock.GetUtcNow().AddMinutes(10.))
        }
        member _.ExchangeBrowserCodeAsync(login, code, accountId, ct) = task {
            if isNull login then raise (AiException("OpenRouter login state is missing."))
            if clock.GetUtcNow() > login.ExpiresAt then raise (AiException("OpenRouter login code expired. Start sign-in again."))
            if String.IsNullOrWhiteSpace code then raise (AiException("OpenRouter authorization code is required."))
            ProviderAuthRuntime.validateAccount accountId
            let endpoint = trusted "key exchange" tokenEndpoint
            use req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            let payload = {| code = code; code_verifier = login.Verifier; code_challenge_method = "S256" |}
            req.Content <- new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            use! response = http.SendAsync(req, ct)
            use! doc = readJson response ct
            return makeSession accountId doc.RootElement
        }
        member _.BeginDeviceLoginAsync(_ct) = Task.FromException<ProviderDeviceAuthorization>(AiException("OpenRouter does not support device authorization."))
        member _.PollDeviceLoginAsync(_device, _accountId, _ct) = Task.FromException<DevicePollResult>(AiException("OpenRouter does not support device authorization."))
        member _.RefreshAsync(_session, _ct) = Task.FromException<ProviderAuthSession>(AiException("OpenRouter OAuth keys do not have a refresh flow. Sign in again to create a new key."))
        member _.DiscoverModelsAsync(session, ct) = task {
            ProviderAuthRuntime.validateSession providerId authority (if isNull session then null else session.AccountId) session
            let baseUrl = if String.IsNullOrWhiteSpace session.ApiBaseUrl then apiBase else session.ApiBaseUrl.TrimEnd('/')
            let baseUri = trusted "API" baseUrl
            use req = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/models")
            req.Headers.Authorization <- System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken)
            use! response = http.SendAsync(req, ct)
            use! doc = readJson response ct
            let mutable data = Unchecked.defaultof<JsonElement>
            let root = doc.RootElement
            let entries = if root.ValueKind = JsonValueKind.Array then root else if root.TryGetProperty("data", &data) && data.ValueKind = JsonValueKind.Array then data else Unchecked.defaultof<JsonElement>
            if entries.ValueKind <> JsonValueKind.Array then return raise (AiException("OpenRouter model discovery returned an invalid response envelope."))
            else
                let results = ResizeArray<ProviderModelDescriptor>()
                for model in entries.EnumerateArray() do
                    let id = OpenRouterAuthHelpers.str model "id" ""
                    if not (String.IsNullOrWhiteSpace id) then
                        let endpoints = OpenRouterAuthHelpers.strings model "supported_endpoints"
                        let endpointText = String.Join(" ", endpoints)
                        let protocol = if endpointText.Contains("responses", StringComparison.OrdinalIgnoreCase) then "openai-responses" else "openai-chat"
                        let mutable architecture = Unchecked.defaultof<JsonElement>
                        let hasArchitecture = model.TryGetProperty("architecture", &architecture)
                        let modalities = if hasArchitecture then OpenRouterAuthHelpers.strings architecture "input_modalities" else OpenRouterAuthHelpers.strings model "input_modalities"
                        let outputModalities = if hasArchitecture then OpenRouterAuthHelpers.strings architecture "output_modalities" else OpenRouterAuthHelpers.strings model "output_modalities"
                        let parameters = OpenRouterAuthHelpers.strings model "supported_parameters"
                        let mutable caps = ModelCapabilities.TextInput
                        if modalities |> Array.exists (fun x -> x.Equals("image", StringComparison.OrdinalIgnoreCase)) then caps <- caps ||| ModelCapabilities.ImageInput
                        if modalities |> Array.exists (fun x -> x.Contains("audio", StringComparison.OrdinalIgnoreCase)) || outputModalities |> Array.exists (fun x -> x.Contains("audio", StringComparison.OrdinalIgnoreCase)) then caps <- caps ||| ModelCapabilities.AudioInput
                        if parameters |> Array.exists (fun x -> x.Contains("tool", StringComparison.OrdinalIgnoreCase)) then caps <- caps ||| ModelCapabilities.ToolCalling
                        if parameters |> Array.exists (fun x -> x.Contains("reason", StringComparison.OrdinalIgnoreCase)) then caps <- caps ||| ModelCapabilities.Reasoning
                        if parameters |> Array.exists (fun x -> x.Contains("structured_output", StringComparison.OrdinalIgnoreCase) || x.Contains("structured_outputs", StringComparison.OrdinalIgnoreCase) || x.Contains("json_schema", StringComparison.OrdinalIgnoreCase)) then caps <- caps ||| ModelCapabilities.StructuredOutput
                        let context = OpenRouterAuthHelpers.int64 model "context_length"
                        let mutable topProvider = Unchecked.defaultof<JsonElement>
                        let hasTopProvider = model.TryGetProperty("top_provider", &topProvider)
                        let maximum = if hasTopProvider then OpenRouterAuthHelpers.int64 topProvider "max_completion_tokens" else OpenRouterAuthHelpers.int64 model "max_output_tokens"
                        let name = OpenRouterAuthHelpers.str model "name" id
                        let descriptor = ProviderModelDescriptor(providerId, id, protocol, baseUrl)
                        descriptor.AccountId <- session.AccountId
                        descriptor.Authority <- authority
                        descriptor.SupportedEndpoints <- endpoints
                        descriptor.ModelInfo <- ModelInfo(providerId, id, name, id, caps, context, maximum, [||])
                        results.Add descriptor
                return results.ToArray() :> IReadOnlyList<ProviderModelDescriptor>
        }
