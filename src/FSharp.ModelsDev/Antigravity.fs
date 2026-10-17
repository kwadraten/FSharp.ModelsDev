namespace FSharp.ModelsDev.Providers

open System
open System.Collections.Generic
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharp.ModelsDev

[<Sealed>]
type AntigravityBackend(model: string, accountId: string, credentials: ICredentialProvider, options: ProviderOptions, httpClient: HttpClient) =
    static let sharedHttpClient = new HttpClient()
    let credentials = if isNull credentials && not (isNull options) then options.Credentials else credentials
    let options = if isNull options then ProviderOptions() else options
    let client = if isNull httpClient then sharedHttpClient else httpClient
    let codec = GeminiCodec(model, "antigravity")
    let mutable discoveredProject: string = null
    let discoveryGate = new SemaphoreSlim(1, 1)
    let baseUrl = if String.IsNullOrWhiteSpace options.BaseUrl then "https://cloudcode-pa.googleapis.com" else options.BaseUrl.TrimEnd('/')
    let rootUrl = baseUrl + "/v1internal"
    let endpoint stream = rootUrl + (if stream then ":streamGenerateContent?alt=sse" else ":generateContent")
    let platform = if OperatingSystem.IsWindows() then "WINDOWS" elif OperatingSystem.IsMacOS() then "MACOS" else "LINUX"
    let addPlatformHeaders (headers: Dictionary<string,string>) =
        headers.["Content-Type"] <- "application/json"
        headers.["User-Agent"] <- "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Antigravity/1.18.3 Chrome/138.0.7204.235 Electron/37.3.1 Safari/537.36"
        headers.["X-Goog-Api-Client"] <- "google-cloud-sdk vscode_cloudshelleditor/0.1"
        headers.["Client-Metadata"] <- $"{{\"ideType\":\"ANTIGRAVITY\",\"platform\":\"{platform}\",\"pluginType\":\"GEMINI\"}}"
    let responseError (response: HttpResponseMessage) (body: string) =
        AiException($"Antigravity returned HTTP {int response.StatusCode}.", int response.StatusCode)
    let discoverProject (token: string) (authHeaders: IReadOnlyDictionary<string,string>) (ct: CancellationToken) = task {
        let body = "{\"metadata\":{\"ideType\":\"ANTIGRAVITY\",\"platform\":\"" + platform + "\",\"pluginType\":\"GEMINI\"}}"
        use request = new HttpRequestMessage(HttpMethod.Post, options.BaseUrl |> fun b -> if String.IsNullOrWhiteSpace b then "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist" else b.TrimEnd('/') + "/v1internal:loadCodeAssist")
        request.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", token)
        let headers = Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        addPlatformHeaders headers
        if not (isNull authHeaders) then
            for KeyValue(k,v) in authHeaders do
                if not (String.Equals(k, "Authorization", StringComparison.OrdinalIgnoreCase) || String.Equals(k, "Content-Type", StringComparison.OrdinalIgnoreCase)) then
                    headers.[k] <- v
        if not (isNull options.Headers) then
            for KeyValue(k,v) in options.Headers do
                if not (String.Equals(k, "Authorization", StringComparison.OrdinalIgnoreCase) || String.Equals(k, "Content-Type", StringComparison.OrdinalIgnoreCase)) then
                    headers.[k] <- v
        for KeyValue(k,v) in headers do
            if not (String.Equals(k, "Content-Type", StringComparison.OrdinalIgnoreCase)) then request.Headers.TryAddWithoutValidation(k,v) |> ignore
        request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        use! response = client.SendAsync(request, ct)
        let! responseBody = response.Content.ReadAsStringAsync(ct)
        if not response.IsSuccessStatusCode then raise (responseError response responseBody)
        use doc = JsonDocument.Parse responseBody
        let mutable prop = Unchecked.defaultof<JsonElement>
        if not (doc.RootElement.TryGetProperty("cloudaicompanionProject", &prop)) then
            raise (AiException("Google Cloud Code Assist did not return cloudaicompanionProject. Set ProviderOptions.ProjectId to your project id."))
        let project =
            if prop.ValueKind = JsonValueKind.String then prop.GetString()
            elif prop.ValueKind = JsonValueKind.Object then
                let mutable id = Unchecked.defaultof<JsonElement>
                if prop.TryGetProperty("projectId", &id) && id.ValueKind = JsonValueKind.String then id.GetString()
                elif prop.TryGetProperty("id", &id) && id.ValueKind = JsonValueKind.String then id.GetString()
                else null
            else null
        if String.IsNullOrWhiteSpace project then
            raise (AiException("Google Cloud Code Assist returned an empty project id. Set ProviderOptions.ProjectId to your project id."))
        return project
    }
    let getAuthAndProject ct = task {
        let mutable session: GoogleOAuthSession = null
        let mutable token: string = null
        let authHeaders = Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        match credentials with
        | :? IGoogleOAuthSessionProvider as sessionProvider ->
            let! loaded = sessionProvider.GetSessionAsync(accountId, ct)
            if isNull loaded then raise (AiException($"No Google OAuth credentials are stored for account '{accountId}'. Sign in first."))
            session <- loaded
            token <- loaded.AccessToken
            if not (String.IsNullOrWhiteSpace token) then authHeaders.["Authorization"] <- "Bearer " + token
        | _ ->
            if isNull credentials then raise (AiException("Antigravity requires Google OAuth credentials."))
            let! headers = credentials.GetHeaders(accountId, ct)
            if not (isNull headers) then
                for KeyValue(k,v) in headers do authHeaders.[k] <- v
                match headers.TryGetValue("Authorization") with
                | true, value when value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) -> token <- value.Substring(7)
                | _ -> ()
        if String.IsNullOrWhiteSpace token then raise (AiException("Antigravity credentials did not provide a bearer token."))
        let configured = if not (String.IsNullOrWhiteSpace options.ProjectId) then options.ProjectId else null
        let fromSession = if isNull session then null else session.ProjectId
        let project =
            if not (String.IsNullOrWhiteSpace configured) then configured
            elif not (String.IsNullOrWhiteSpace fromSession) then fromSession
            else discoveredProject
        if String.IsNullOrWhiteSpace project then
            do! discoveryGate.WaitAsync ct
            try
                if not (String.IsNullOrWhiteSpace discoveredProject) then return token, discoveredProject, authHeaders
                else
                    let! found = discoverProject token authHeaders ct
                    discoveredProject <- found
                    return token, found, authHeaders
            finally
                discoveryGate.Release() |> ignore
        else return token, project, authHeaders
    }
    new(model: string, accountId: string, credentials: ICredentialProvider, options: ProviderOptions) =
        new AntigravityBackend(model, accountId, credentials, options, null)
    member _.Model = model
    member _.AccountId = accountId
    member internal _.DiscoverModelsAsync(ct: CancellationToken) : Task<IReadOnlyList<ProviderModelDescriptor>> = task {
        let! token, project, authHeaders = getAuthAndProject ct
        use request = new HttpRequestMessage(HttpMethod.Post, rootUrl + ":fetchAvailableModels")
        let headers = Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        addPlatformHeaders headers
        for KeyValue(k,v) in authHeaders do headers.[k] <- v
        if not (isNull options.Headers) then
            for KeyValue(k,v) in options.Headers do headers.[k] <- v
        headers.["Authorization"] <- "Bearer " + token
        for KeyValue(k,v) in headers do
            if not (String.Equals(k, "Content-Type", StringComparison.OrdinalIgnoreCase)) then
                request.Headers.TryAddWithoutValidation(k,v) |> ignore
        request.Content <- new StringContent(JsonSerializer.Serialize({| project = project |}), Encoding.UTF8, "application/json")
        use! response = client.SendAsync(request, ct)
        if not response.IsSuccessStatusCode then raise (responseError response "")
        let! body = response.Content.ReadAsStringAsync(ct)
        let models = ResizeArray<ProviderModelDescriptor>()
        try
            use doc = JsonDocument.Parse(body)
            let mutable entries = Unchecked.defaultof<JsonElement>
            if doc.RootElement.ValueKind <> JsonValueKind.Object
               || not (doc.RootElement.TryGetProperty("models", &entries))
               || entries.ValueKind <> JsonValueKind.Object then
                raise (AiException("Antigravity returned an invalid model list."))
            for entry in entries.EnumerateObject() do
                if entry.Value.ValueKind = JsonValueKind.Object && not (String.IsNullOrWhiteSpace entry.Name) then
                    let mutable internalFlag = Unchecked.defaultof<JsonElement>
                    let isInternal = entry.Value.TryGetProperty("isInternal", &internalFlag) && internalFlag.ValueKind = JsonValueKind.True
                    if not isInternal then
                        let descriptor = ProviderModelDescriptor("antigravity", entry.Name, "antigravity", baseUrl)
                        descriptor.AccountId <- accountId
                        descriptor.Authority <- baseUrl
                        // Unknown capabilities and limits stay unknown; never infer them from a model ID.
                        models.Add(descriptor)
        with :? JsonException -> raise (AiException("Antigravity returned an invalid model list."))
        return models |> Seq.sortBy (fun item -> item.ModelId) |> Seq.toArray |> Array.AsReadOnly :> IReadOnlyList<ProviderModelDescriptor>
    }
    interface IChatBackend with
        member _.PrepareRequest(request, stream, ct) = task {
            let! token, project, authHeaders = getAuthAndProject ct
            let headers = Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            addPlatformHeaders headers
            for KeyValue(k,v) in authHeaders do
                if not (String.Equals(k, "Content-Type", StringComparison.OrdinalIgnoreCase)) then headers.[k] <- v
            headers.["Authorization"] <- "Bearer " + token
            let requestJson = codec.SerializeRequest(request)
            use reqDoc = JsonDocument.Parse requestJson
            use buffer = new IO.MemoryStream()
            use writer = new Utf8JsonWriter(buffer)
            writer.WriteStartObject()
            writer.WriteString("model", request.Model)
            writer.WriteString("project", project)
            writer.WritePropertyName("request")
            reqDoc.RootElement.WriteTo writer
            writer.WriteString("requestId", Guid.NewGuid().ToString())
            writer.WriteString("userAgent", "antigravity")
            writer.WriteEndObject()
            writer.Flush()
            let body = Encoding.UTF8.GetString(buffer.ToArray())
            return PreparedChatRequest(endpoint stream, headers :> IReadOnlyDictionary<string,string>, body)
        }
        member _.ParseResponse(body) = codec.ParseResponseBody(body)
        member _.CreateStreamParser() = codec.MakeStreamParser()
    interface IRequestScopedBackend with
        member _.ResolveBackend(requestedModel) =
            new AntigravityBackend(requestedModel, accountId, credentials, options, httpClient) :> IChatBackend
    interface IDisposable with
        member _.Dispose() =
            discoveryGate.Dispose()

/// Explicit account-scoped model discovery using Google OAuth and the Cloud Code service.
/// Constructing this object does not obtain credentials or send HTTP requests.
[<Sealed>]
type AntigravityModelDiscovery(credentials: ICredentialProvider, options: ProviderOptions, httpClient: HttpClient) =
    new(credentials: ICredentialProvider, options: ProviderOptions) = AntigravityModelDiscovery(credentials, options, null)
    member _.DiscoverModelsAsync(accountId: string, cancellationToken: CancellationToken) : Task<IReadOnlyList<ProviderModelDescriptor>> = task {
        if String.IsNullOrWhiteSpace accountId then invalidArg "accountId" "An account id is required."
        cancellationToken.ThrowIfCancellationRequested()
        use backend = new AntigravityBackend("", accountId, credentials, options, httpClient)
        return! backend.DiscoverModelsAsync(cancellationToken)
    }
    member this.DiscoverModelsAsync(accountId: string) = this.DiscoverModelsAsync(accountId, CancellationToken.None)
