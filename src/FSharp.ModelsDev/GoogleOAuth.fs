namespace FSharp.ModelsDev.Providers

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Globalization
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharp.ModelsDev

/// OAuth settings. The host supplies its OAuth client id and, when required, client secret.
[<Sealed; AllowNullLiteral>]
type GoogleOAuthOptions() =
    member val ClientId: string = null with get, set
    member val ClientSecret: string = null with get, set
    member val RedirectUri = "http://localhost:51121/oauth-callback" with get, set
    member val Scopes: IReadOnlyList<string> =
        [| "https://www.googleapis.com/auth/cloud-platform"
           "https://www.googleapis.com/auth/userinfo.email"
           "https://www.googleapis.com/auth/userinfo.profile"
           "https://www.googleapis.com/auth/cclog"
           "https://www.googleapis.com/auth/experimentsandconfigs" |] with get, set
    member val AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/auth" with get, set
    member val TokenEndpoint = "https://oauth2.googleapis.com/token" with get, set
    member val HttpClient: HttpClient = null with get, set
    member val TimeProvider = TimeProvider.System with get, set

[<Sealed; AllowNullLiteral>]
type GoogleOAuthSession(accountId: string, accessToken: string, refreshToken: string, expiresAt: DateTimeOffset, projectId: string) =
    member _.AccountId = accountId
    member val AccessToken = accessToken with get, set
    member val RefreshToken = refreshToken with get, set
    member val ExpiresAt = expiresAt with get, set
    member val ProjectId = projectId with get, set

/// Persistence boundary for OAuth tokens. The default implementation is intentionally process-local.
type IOAuthTokenStore =
    abstract LoadAsync: accountId: string * cancellationToken: CancellationToken -> Task<GoogleOAuthSession>
    abstract SaveAsync: session: GoogleOAuthSession * cancellationToken: CancellationToken -> Task
    abstract DeleteAsync: accountId: string * cancellationToken: CancellationToken -> Task

[<Sealed>]
type InMemoryOAuthTokenStore() =
    let sessions = ConcurrentDictionary<string, GoogleOAuthSession>(StringComparer.Ordinal)
    let clone (s: GoogleOAuthSession) = GoogleOAuthSession(s.AccountId, s.AccessToken, s.RefreshToken, s.ExpiresAt, s.ProjectId)
    interface IOAuthTokenStore with
        member _.LoadAsync(accountId, ct) =
            ct.ThrowIfCancellationRequested()
            match sessions.TryGetValue accountId with
            | true, value -> Task.FromResult(clone value)
            | _ -> Task.FromResult(null)
        member _.SaveAsync(session, ct) =
            ct.ThrowIfCancellationRequested()
            sessions.[session.AccountId] <- clone session
            Task.CompletedTask
        member _.DeleteAsync(accountId, ct) =
            ct.ThrowIfCancellationRequested()
            sessions.TryRemove(accountId) |> ignore
            Task.CompletedTask

/// An authorization attempt. State and verifier are held separately and are consumed once.
[<Sealed; AllowNullLiteral>]
type GoogleOAuthLoginSession internal (authorizationUrl: Uri, state: string, verifier: string, createdAt: DateTimeOffset) =
    let mutable consumed = 0
    member _.AuthorizationUrl = authorizationUrl
    member internal _.State = state
    member internal _.Verifier = verifier
    member internal _.CreatedAt = createdAt
    member internal _.TryConsume() = Interlocked.CompareExchange(&consumed, 1, 0) = 0

type IGoogleOAuthSessionProvider =
    abstract GetSessionAsync: accountId: string * cancellationToken: CancellationToken -> Task<GoogleOAuthSession>
    /// Invalidates the cached access token so the next request refreshes it.
    abstract Invalidate: accountId: string -> unit

[<Sealed>]
type GoogleOAuthClient(options: GoogleOAuthOptions, tokenStore: IOAuthTokenStore, injectedHttpClient: HttpClient, injectedTimeProvider: TimeProvider) =
    let options = if isNull options then GoogleOAuthOptions() else options
    let client =
        if not (isNull injectedHttpClient) then injectedHttpClient
        elif not (isNull options.HttpClient) then options.HttpClient
        else new HttpClient()
    let ownsClient = isNull injectedHttpClient && isNull options.HttpClient
    let clock = if isNull injectedTimeProvider then options.TimeProvider else injectedTimeProvider
    let store = if isNull (box tokenStore) then InMemoryOAuthTokenStore() :> IOAuthTokenStore else tokenStore
    let validateClientId () =
        if String.IsNullOrWhiteSpace options.ClientId then
            invalidArg "ClientId" "Configure GoogleOAuthOptions.ClientId before starting OAuth login or refreshing tokens."
    let lifetime = TimeSpan.FromMinutes 5.
    let randomUrlSafe count =
        let bytes = RandomNumberGenerator.GetBytes count
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    let encode (value: string) = Uri.EscapeDataString value
    let parseQuery (uri: Uri) =
        let result = Dictionary<string, string>(StringComparer.Ordinal)
        let query = uri.Query.TrimStart('?')
        if query <> "" then
            for item in query.Split('&', StringSplitOptions.RemoveEmptyEntries) do
                let pair = item.Split('=', 2)
                let key = Uri.UnescapeDataString(pair.[0].Replace('+', ' '))
                let value = if pair.Length > 1 then Uri.UnescapeDataString(pair.[1].Replace('+', ' ')) else ""
                result.[key] <- value
        result
    let readTokenResponse (body: string) (accountId: string) (previousRefresh: string) (ct: CancellationToken) = task {
        use doc = JsonDocument.Parse body
        let root = doc.RootElement
        let getString (key: string) =
            let mutable p = Unchecked.defaultof<JsonElement>
            if root.TryGetProperty(key, &p) && p.ValueKind = JsonValueKind.String then p.GetString() else null
        let access = getString "access_token"
        if String.IsNullOrWhiteSpace access then raise (AiException("Google OAuth token response did not include access_token."))
        let refresh = match getString "refresh_token" with null | "" -> previousRefresh | value -> value
        let mutable expires = 3600
        let mutable expiresElement = Unchecked.defaultof<JsonElement>
        if root.TryGetProperty("expires_in", &expiresElement) then
            match expiresElement.TryGetInt32() with true, n when n > 0 -> expires <- n | _ -> ()
        let session = GoogleOAuthSession(accountId, access, refresh, clock.GetUtcNow().AddSeconds(float expires), null)
        do! store.SaveAsync(session, ct)
        return session
    }
    let postToken (fields: (string * string) list) (accountId: string) (previousRefresh: string) (ct: CancellationToken) = task {
        ct.ThrowIfCancellationRequested()
        validateClientId ()
        let formFields =
            fields
            |> Seq.filter (fun (key, value) -> key <> "client_secret" || not (String.IsNullOrWhiteSpace value))
            |> Seq.map (fun (key, value) -> KeyValuePair<string,string>(key, value))
        use content = new FormUrlEncodedContent(formFields)
        use! response = client.PostAsync(options.TokenEndpoint, content, ct)
        let! body = response.Content.ReadAsStringAsync(ct)
        if not response.IsSuccessStatusCode then
            let code = int response.StatusCode
            let description =
                try
                    use doc = JsonDocument.Parse body
                    let mutable err = Unchecked.defaultof<JsonElement>
                    if doc.RootElement.TryGetProperty("error", &err) then err.GetString() else "OAuth token request failed"
                with _ -> "OAuth token request failed"
            raise (AiException($"Google OAuth {description}. Check the account authorization and try signing in again.", code))
        return! readTokenResponse body accountId previousRefresh ct
    }
    new(options: GoogleOAuthOptions, tokenStore: IOAuthTokenStore) = new GoogleOAuthClient(options, tokenStore, null, null)
    new(options: GoogleOAuthOptions, tokenStore: IOAuthTokenStore, httpClient: HttpClient) = new GoogleOAuthClient(options, tokenStore, httpClient, null)
    member _.BeginLogin() =
        validateClientId ()
        let state, verifier = randomUrlSafe 32, randomUrlSafe 64
        let challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes verifier)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        let scopes = String.Join(" ", options.Scopes)
        let pairs =
            [ "client_id", options.ClientId; "redirect_uri", options.RedirectUri; "response_type", "code"
              "scope", scopes; "access_type", "offline"; "prompt", "consent"
              "state", state; "code_challenge", challenge; "code_challenge_method", "S256" ]
        let query = pairs |> List.map (fun (k,v) -> encode k + "=" + encode v) |> String.concat "&"
        GoogleOAuthLoginSession(Uri(options.AuthorizationEndpoint + "?" + query), state, verifier, clock.GetUtcNow())
    member _.CompleteLoginAsync(session: GoogleOAuthLoginSession, callbackUrl: string, accountId: string, cancellationToken: CancellationToken) = task {
        cancellationToken.ThrowIfCancellationRequested()
        if isNull session then nullArg "session"
        if String.IsNullOrWhiteSpace accountId then invalidArg "accountId" "An account id is required to isolate OAuth credentials."
        if not (session.TryConsume()) then raise (AiException("This Google OAuth callback has already been used."))
        if clock.GetUtcNow() - session.CreatedAt > lifetime then raise (AiException("Google OAuth login expired after five minutes; begin a new login."))
        let callback =
            match Uri.TryCreate(callbackUrl, UriKind.Absolute) with
            | true, uri -> uri
            | _ -> raise (AiException("Google OAuth callback URL is invalid."))
        let redirect = Uri(options.RedirectUri)
        if callback.Scheme <> redirect.Scheme || callback.Host <> redirect.Host || callback.Port <> redirect.Port || callback.AbsolutePath <> redirect.AbsolutePath then
            raise (AiException("Google OAuth callback did not match the configured redirect URI."))
        let values = parseQuery callback
        let value key = match values.TryGetValue key with true, v -> v | _ -> ""
        let oauthError = value "error"
        if oauthError <> "" then raise (AiException($"Google OAuth login failed: {oauthError}."))
        if not (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value "state"), Encoding.UTF8.GetBytes(session.State))) then
            raise (AiException("Google OAuth callback state did not match; the login was rejected."))
        if String.IsNullOrWhiteSpace(value "code") then raise (AiException("Google OAuth callback did not include an authorization code."))
        return! postToken
            [ "client_id", options.ClientId; "client_secret", options.ClientSecret; "code", value "code"
              "code_verifier", session.Verifier; "grant_type", "authorization_code"; "redirect_uri", options.RedirectUri ] accountId null cancellationToken
    }
    member this.LoginAsync(openBrowser: Func<Uri, CancellationToken, Task>, accountId: string, cancellationToken: CancellationToken) = task {
        let redirect = Uri(options.RedirectUri)
        let parsedIp = match IPAddress.TryParse redirect.Host with true, address -> address | _ -> null
        if redirect.Scheme <> Uri.UriSchemeHttp || (redirect.Host <> "localhost" && (isNull parsedIp || not (IPAddress.IsLoopback parsedIp))) then
            invalidArg "RedirectUri" "Loopback login requires an http://localhost or loopback IP redirect URI."
        let ip = if redirect.Host = "localhost" then IPAddress.Loopback else parsedIp
        let host = if ip.Equals(IPAddress.Loopback) then "localhost" else ip.ToString()
        let prefix = $"http://{host}:{redirect.Port}/"
        use listener = new HttpListener()
        listener.Prefixes.Add prefix
        listener.Start()
        use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
        timeout.CancelAfter lifetime
        let login = this.BeginLogin()
        let receiveCallback = task {
            let mutable acceptedContext: HttpListenerContext = null
            let mutable accepted = false
            while not accepted do
                let! context = listener.GetContextAsync().WaitAsync(timeout.Token)
                if context.Request.Url.AbsolutePath = redirect.AbsolutePath then
                    acceptedContext <- context
                    accepted <- true
                else
                    context.Response.StatusCode <- int HttpStatusCode.NotFound
                    context.Response.Close()
            let callback = acceptedContext.Request.Url
            let body = "<html><body>Google sign-in received. You can close this window.</body></html>"
            let bytes = Encoding.UTF8.GetBytes body
            acceptedContext.Response.ContentType <- "text/html; charset=utf-8"
            acceptedContext.Response.ContentLength64 <- int64 bytes.Length
            do! acceptedContext.Response.OutputStream.WriteAsync(bytes.AsMemory(), timeout.Token)
            acceptedContext.Response.Close()
            return callback
        }
        try
            do! openBrowser.Invoke(login.AuthorizationUrl, timeout.Token)
            let! callback = receiveCallback
            return! this.CompleteLoginAsync(login, callback.ToString(), accountId, timeout.Token)
        with :? OperationCanceledException ->
            if cancellationToken.IsCancellationRequested then return raise (OperationCanceledException(cancellationToken))
            else return raise (AiException("Google OAuth login timed out after five minutes."))
    }
    member internal _.HttpClient = client
    member internal _.Clock = clock
    member internal _.Store = store
    member internal _.PostRefresh(accountId, refreshToken, ct) =
        postToken [ "client_id", options.ClientId; "client_secret", options.ClientSecret; "refresh_token", refreshToken; "grant_type", "refresh_token" ] accountId refreshToken ct
    interface IDisposable with member _.Dispose() = if ownsClient then client.Dispose()

[<Sealed>]
type GoogleOAuthCredentialProvider(options: GoogleOAuthOptions, tokenStore: IOAuthTokenStore, httpClient: HttpClient, timeProvider: TimeProvider) as this =
    let client = new GoogleOAuthClient(options, tokenStore, httpClient, timeProvider)
    let gates = ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal)
    let invalidated = ConcurrentDictionary<string, bool>(StringComparer.Ordinal)
    new(options: GoogleOAuthOptions, tokenStore: IOAuthTokenStore) = new GoogleOAuthCredentialProvider(options, tokenStore, null, null)
    new(options: GoogleOAuthOptions, tokenStore: IOAuthTokenStore, httpClient: HttpClient) = new GoogleOAuthCredentialProvider(options, tokenStore, httpClient, null)
    member _.Client = client
    member private _.GetFreshSession(accountId: string, ct: CancellationToken) = task {
        if String.IsNullOrWhiteSpace accountId then invalidArg "accountId" "A non-empty account id is required."
        let gate = gates.GetOrAdd(accountId, fun _ -> new SemaphoreSlim(1,1))
        do! gate.WaitAsync ct
        use releaseGate = { new IDisposable with member _.Dispose() = gate.Release() |> ignore }
        let! current = client.Store.LoadAsync(accountId, ct)
        if isNull current then
            return raise (AiException($"No Google OAuth credentials are stored for account '{accountId}'. Sign in first."))
        else
            let needsRefresh = invalidated.ContainsKey accountId || current.ExpiresAt <= client.Clock.GetUtcNow().AddSeconds 60.
            if not needsRefresh then return current
            else
                if String.IsNullOrWhiteSpace current.RefreshToken then
                    return raise (AiException($"Google OAuth credentials for account '{accountId}' expired and have no refresh token. Sign in again."))
                else
                    try
                        let! refreshed = client.PostRefresh(accountId, current.RefreshToken, ct)
                        if String.IsNullOrWhiteSpace refreshed.ProjectId then refreshed.ProjectId <- current.ProjectId
                        do! client.Store.SaveAsync(refreshed, ct)
                        invalidated.TryRemove(accountId) |> ignore
                        return refreshed
                    with ex ->
                        if ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) then
                            do! client.Store.DeleteAsync(accountId, ct)
                            return raise (AiException($"Google refresh token for account '{accountId}' was rejected (invalid_grant). Sign in again."))
                        else return raise ex
    }
    interface IGoogleOAuthSessionProvider with
        member _.GetSessionAsync(accountId, ct) = this.GetFreshSession(accountId, ct)
        member _.Invalidate(accountId) = invalidated.[accountId] <- true
    interface ICredentialProvider with
        member this.GetHeaders(accountId, ct) = task {
            let! session = this.GetFreshSession(accountId, ct)
            let headers = Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            headers.["Authorization"] <- "Bearer " + session.AccessToken
            return headers :> IReadOnlyDictionary<string,string>
        }
    interface IDisposable with
        member _.Dispose() = (client :> IDisposable).Dispose()
