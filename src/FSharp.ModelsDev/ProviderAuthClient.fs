namespace FSharp.ModelsDev.Providers

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Globalization
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open FSharp.ModelsDev

/// Provider OAuth/device authorization client. Construction is offline; network access starts with a login or request.
[<Sealed>]
type ProviderAuthClient(options: ProviderAuthOptions, store: IProviderAuthSessionStore, injectedHttp: HttpClient, injectedClock: TimeProvider) as this =
    let opts = if isNull options then nullArg "options" else options
    let authOptions = ProviderAuthOptions(opts.ProviderId)
    do
        authOptions.ClientId <- opts.ClientId
        authOptions.Issuer <- opts.Issuer
        authOptions.AuthorizationEndpoint <- opts.AuthorizationEndpoint
        authOptions.TokenEndpoint <- opts.TokenEndpoint
        authOptions.CopilotTokenEndpoint <- opts.CopilotTokenEndpoint
        authOptions.DeviceAuthorizationEndpoint <- opts.DeviceAuthorizationEndpoint
        authOptions.ApiBaseUrl <- opts.ApiBaseUrl
        authOptions.RedirectUri <- opts.RedirectUri
        authOptions.Scopes <- if isNull opts.Scopes then Array.empty else opts.Scopes |> Seq.toArray
        authOptions.EnterpriseDomain <- opts.EnterpriseDomain
        authOptions.OrganizationId <- opts.OrganizationId
        authOptions.DeviceId <- opts.DeviceId
        authOptions.ClientVersion <- opts.ClientVersion
        authOptions.TrustedOrigins <- if isNull opts.TrustedOrigins then Array.empty else opts.TrustedOrigins |> Seq.toArray
        authOptions.LoginTimeout <- opts.LoginTimeout
        authOptions.RefreshSkew <- opts.RefreshSkew
        if authOptions.LoginTimeout <= TimeSpan.Zero then invalidArg "options.LoginTimeout" "LoginTimeout must be positive."
        if authOptions.RefreshSkew < TimeSpan.Zero then invalidArg "options.RefreshSkew" "RefreshSkew cannot be negative."
        if String.IsNullOrWhiteSpace authOptions.DeviceId then authOptions.DeviceId <- Guid.NewGuid().ToString("N")
    let http = if isNull injectedHttp then new HttpClient(new HttpClientHandler(AllowAutoRedirect = false)) else injectedHttp
    let ownsHttp = isNull injectedHttp
    let clock = if isNull injectedClock then TimeProvider.System else injectedClock
    let sessionStore = if isNull store then InMemoryProviderAuthSessionStore() :> IProviderAuthSessionStore else store
    let adapter = ProviderAuthAdapters.create authOptions http clock
    let providerId = authOptions.ProviderId
    let authority = authOptions.Authority
    let activeLogins = ConcurrentDictionary<ProviderBrowserLogin, byte>(HashIdentity.Reference)
    let activeDevices = ConcurrentDictionary<ProviderDeviceAuthorization, byte>(HashIdentity.Reference)
    let validateAccount = ProviderAuthRuntime.validateAccount
    let ensureLogin login =
        if isNull login then nullArg "login"
        if not (activeLogins.ContainsKey login) then invalidOp "This browser login was not created by this client, or has already been completed."
        if clock.GetUtcNow() >= login.ExpiresAt then
            activeLogins.TryRemove(login) |> ignore
            invalidOp "Provider browser login expired. Begin a new login."
    let saveReturned accountId (session: ProviderAuthSession) (ct: CancellationToken) = task {
        if isNull session then invalidOp "Provider authorization did not return a session."
        if not (String.Equals(session.ProviderId, providerId, StringComparison.Ordinal)) then invalidOp "Provider authorization returned a session for a different provider."
        if not (String.Equals(session.Authority, authority, StringComparison.Ordinal)) then invalidOp "Provider authorization returned a session for a different authority."
        if not (String.Equals(session.AccountId, accountId, StringComparison.Ordinal)) then invalidOp "Provider authorization returned a session for a different account."
        if String.IsNullOrWhiteSpace session.DeviceId then session.DeviceId <- authOptions.DeviceId
        do! sessionStore.SaveAsync(ProviderAuthRuntime.cloneSession session, ct)
        return ProviderAuthRuntime.cloneSession session
    }
    new(options: ProviderAuthOptions) = new ProviderAuthClient(options, null, null, null)
    new(options: ProviderAuthOptions, store: IProviderAuthSessionStore) = new ProviderAuthClient(options, store, null, null)
    new(options: ProviderAuthOptions, store: IProviderAuthSessionStore, httpClient: HttpClient) = new ProviderAuthClient(options, store, httpClient, null)

    member _.BeginBrowserLoginAsync(cancellationToken: CancellationToken) = task {
        cancellationToken.ThrowIfCancellationRequested()
        if not adapter.SupportsBrowser then raise (NotSupportedException $"{providerId} does not support browser login.")
        let! login = adapter.BeginBrowserLoginAsync cancellationToken
        if isNull login then invalidOp "Provider adapter did not create a browser login."
        activeLogins.TryAdd(login, 0uy) |> ignore
        return login
    }
    member this.BeginBrowserLoginAsync() = this.BeginBrowserLoginAsync(CancellationToken.None)

    /// Completes an authorization callback URL, or a provider's manual callback/code URL.
    member _.CompleteBrowserLoginAsync(login: ProviderBrowserLogin, callbackUrl: string, accountId: string, cancellationToken: CancellationToken) = task {
        cancellationToken.ThrowIfCancellationRequested()
        validateAccount accountId
        ensureLogin login
        if String.IsNullOrWhiteSpace callbackUrl then invalidArg "callbackUrl" "The provider callback or authorization code is required."
        if clock.GetUtcNow() >= login.ExpiresAt then invalidOp "Provider browser login expired. Begin a new login."
        let query = Dictionary<string,string>(StringComparer.Ordinal)
        let redirectText = login.RedirectUri
        if String.IsNullOrWhiteSpace redirectText then
            query.["code"] <- callbackUrl
        else
            let callback =
                match Uri.TryCreate(callbackUrl, UriKind.Absolute) with
                | true, uri -> uri
                | _ -> invalidArg "callbackUrl" "The callback must be an absolute URL."
            let redirect = Uri(redirectText)
            if not (String.Equals(callback.Scheme, redirect.Scheme, StringComparison.OrdinalIgnoreCase)
                    && String.Equals(callback.Host, redirect.Host, StringComparison.OrdinalIgnoreCase)
                    && callback.Port = redirect.Port && callback.AbsolutePath = redirect.AbsolutePath) then
                invalidOp "Provider callback did not match the redirect URI created for this login."
            for pair in callback.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries) do
                let bits = pair.Split('=', 2)
                let decode (s: string) = Uri.UnescapeDataString(s.Replace('+', ' '))
                query.[decode bits.[0]] <- if bits.Length > 1 then decode bits.[1] else ""
        let get key = match query.TryGetValue key with true, value -> value | _ -> ""
        if not (String.IsNullOrWhiteSpace redirectText) then
            let actual = Encoding.UTF8.GetBytes(get "state")
            let expected = Encoding.UTF8.GetBytes(login.State)
            if actual.Length <> expected.Length || not (CryptographicOperations.FixedTimeEquals(actual, expected)) then
                invalidOp "Provider callback state did not match; the login was rejected."
        if not (login.TryConsume()) then
            activeLogins.TryRemove(login) |> ignore
            invalidOp "This provider browser callback has already been used."
        activeLogins.TryRemove(login) |> ignore
        if not (String.IsNullOrEmpty(get "error")) then
            raise (AiException(ProviderAuthRuntime.oauthFailureMessage providerId (get "error")))
        let code = get "code"
        if String.IsNullOrWhiteSpace code then invalidOp "Provider callback did not include an authorization code."
        let! session = adapter.ExchangeBrowserCodeAsync(login, code, accountId, cancellationToken)
        return! saveReturned accountId session cancellationToken
    }
    member this.CompleteBrowserLoginAsync(login, callbackUrl, accountId) = this.CompleteBrowserLoginAsync(login, callbackUrl, accountId, CancellationToken.None)

    member this.LoginWithBrowserAsync(openBrowser: Func<Uri, CancellationToken, Task>, accountId: string, cancellationToken: CancellationToken) = task {
        if isNull openBrowser then nullArg "openBrowser"
        validateAccount accountId
        let redirect = Uri(authOptions.RedirectUri)
        let mutable parsed = Unchecked.defaultof<IPAddress>
        if redirect.Scheme <> Uri.UriSchemeHttp || (redirect.Host <> "localhost" && (not (IPAddress.TryParse(redirect.Host, &parsed)) || not (IPAddress.IsLoopback parsed))) then
            invalidArg "RedirectUri" "Loopback login requires an http://localhost or loopback IP redirect URI."
        let ip = if redirect.Host = "localhost" then IPAddress.Loopback else parsed
        let host = if ip.Equals(IPAddress.Loopback) then "localhost" else ip.ToString()
        use listener = new HttpListener()
        listener.Prefixes.Add($"http://{host}:{redirect.Port}/")
        listener.Start()
        use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
        timeout.CancelAfter authOptions.LoginTimeout
        let! login = this.BeginBrowserLoginAsync(timeout.Token)
        let receive = task {
            let mutable context: HttpListenerContext = null
            while isNull context do
                let! next = listener.GetContextAsync().WaitAsync(timeout.Token)
                if next.Request.Url.AbsolutePath = redirect.AbsolutePath then context <- next
                else
                    next.Response.StatusCode <- int HttpStatusCode.NotFound
                    next.Response.Close()
            let bytes = Encoding.UTF8.GetBytes("<html><body>Sign-in received. You can close this window.</body></html>")
            context.Response.ContentType <- "text/html; charset=utf-8"
            context.Response.ContentLength64 <- int64 bytes.Length
            do! context.Response.OutputStream.WriteAsync(bytes.AsMemory(), timeout.Token)
            context.Response.Close()
            return context.Request.Url.ToString()
        }
        try
            do! openBrowser.Invoke(login.AuthorizationUrl, timeout.Token)
            let! callback = receive
            return! this.CompleteBrowserLoginAsync(login, callback, accountId, timeout.Token)
        with :? OperationCanceledException ->
            if cancellationToken.IsCancellationRequested then return raise (OperationCanceledException(cancellationToken))
            else return raise (AiException($"{providerId} browser login timed out."))
    }
    member this.LoginWithBrowserAsync(openBrowser, accountId: string) = this.LoginWithBrowserAsync(openBrowser, accountId, CancellationToken.None)

    member _.BeginDeviceLoginAsync(cancellationToken: CancellationToken) = task {
        cancellationToken.ThrowIfCancellationRequested()
        if not adapter.SupportsDevice then raise (NotSupportedException $"{providerId} does not support device login.")
        let! device = adapter.BeginDeviceLoginAsync cancellationToken
        if isNull device then invalidOp "Provider adapter did not create a device authorization."
        activeDevices.TryAdd(device, 0uy) |> ignore
        return device
    }
    member this.BeginDeviceLoginAsync() = this.BeginDeviceLoginAsync(CancellationToken.None)

    member _.CompleteDeviceLoginAsync(device: ProviderDeviceAuthorization, accountId: string, cancellationToken: CancellationToken) = task {
        validateAccount accountId
        if isNull device then nullArg "device"
        if not adapter.SupportsDevice then raise (NotSupportedException $"{providerId} does not support device login.")
        if not (activeDevices.TryRemove device |> fst) then invalidOp "This device authorization was not created by this client, or has already been completed."
        let mutable interval = device.Interval
        let mutable result: ProviderAuthSession option = None
        while clock.GetUtcNow() < device.ExpiresAt && result.IsNone do
            let remaining = device.ExpiresAt - clock.GetUtcNow()
            let delay = if interval < remaining then interval else remaining
            do! Task.Delay(delay, clock, cancellationToken)
            if clock.GetUtcNow() >= device.ExpiresAt then raise (AiException($"{providerId} device authorization expired."))
            let! pollResult = adapter.PollDeviceLoginAsync(device, accountId, cancellationToken)
            match pollResult with
            | DevicePollResult.Pending -> ()
            | DevicePollResult.SlowDown -> interval <- interval + TimeSpan.FromSeconds 5.
            | DevicePollResult.Denied -> raise (AiException($"{providerId} device authorization was denied."))
            | DevicePollResult.Expired -> raise (AiException($"{providerId} device authorization expired."))
            | DevicePollResult.Authorized session -> result <- Some session
        match result with
        | Some session ->
            if adapter.CanRefresh && String.IsNullOrWhiteSpace session.AccessToken then
                let! exchanged = adapter.RefreshAsync(session, cancellationToken)
                return! saveReturned accountId exchanged cancellationToken
            else return! saveReturned accountId session cancellationToken
        | None -> return raise (AiException($"{providerId} device authorization expired."))
    }
    member this.CompleteDeviceLoginAsync(device, accountId: string) = this.CompleteDeviceLoginAsync(device, accountId, CancellationToken.None)

    member private _.GetFreshSession(accountId: string, cancellationToken: CancellationToken) = task {
        validateAccount accountId
        let gate = ProviderAuthRuntime.gateFor sessionStore providerId authority accountId
        do! gate.WaitAsync cancellationToken
        use release = { new IDisposable with member _.Dispose() = gate.Release() |> ignore }
        let! loaded = sessionStore.LoadAsync(providerId, authority, accountId, cancellationToken)
        ProviderAuthRuntime.validateSession providerId authority accountId loaded
        let current = ProviderAuthRuntime.cloneSession loaded
        let expired = current.ExpiresAt.HasValue && current.ExpiresAt.Value <= clock.GetUtcNow()
        let mustRefresh = adapter.CanRefresh && (ProviderAuthRuntime.isInvalidated sessionStore providerId authority accountId || (current.ExpiresAt.HasValue && current.ExpiresAt.Value <= clock.GetUtcNow().Add(authOptions.RefreshSkew)))
        if expired && not adapter.CanRefresh then
            return raise (AiException($"{providerId} credentials expired and cannot be refreshed. Sign in again."))
        elif not mustRefresh then
            return current
        elif String.IsNullOrWhiteSpace current.RefreshToken && String.IsNullOrWhiteSpace current.UpstreamToken then
            return raise (AiException($"{providerId} credentials expired and cannot be refreshed. Sign in again."))
        else
            let! attempt = task {
                try
                    let! refreshed = adapter.RefreshAsync(current, cancellationToken)
                    if isNull refreshed then invalidOp "Provider refresh did not return a session."
                    if String.IsNullOrWhiteSpace refreshed.RefreshToken then refreshed.RefreshToken <- current.RefreshToken
                    if String.IsNullOrWhiteSpace refreshed.UpstreamToken then refreshed.UpstreamToken <- current.UpstreamToken
                    if String.IsNullOrWhiteSpace refreshed.DeviceId then refreshed.DeviceId <- current.DeviceId
                    let! saved = saveReturned accountId refreshed cancellationToken
                    return Choice1Of2 saved
                with ex -> return Choice2Of2 ex
            }
            match attempt with
            | Choice1Of2 saved ->
                ProviderAuthRuntime.clearInvalidation sessionStore providerId authority accountId
                return saved
            | Choice2Of2 (:? OperationCanceledException as ex) -> return raise ex
            | Choice2Of2 ex ->
                match ProviderAuthRuntime.sanitizedOAuthError ex with
                | Some "invalid_grant" ->
                    do! sessionStore.DeleteAsync(providerId, authority, accountId, cancellationToken)
                    return raise (AiException(ProviderAuthRuntime.oauthFailureMessage providerId "invalid_grant"))
                | Some code -> return raise (AiException(ProviderAuthRuntime.oauthFailureMessage providerId code))
                | None -> return raise (AiException($"{providerId} credentials could not be refreshed. Sign in again."))
    }
    member this.GetSessionAsync(accountId: string, cancellationToken: CancellationToken) = task {
        let! session = this.GetFreshSession(accountId, cancellationToken)
        return ProviderAuthRuntime.cloneSession session
    }
    member this.GetSessionAsync(accountId: string) = this.GetSessionAsync(accountId, CancellationToken.None)

    member _.CreateCredentialProvider() =
        { new IRefreshableCredentialProvider with
            member _.CanRefresh = adapter.CanRefresh
            member _.Invalidate(accountId) = if not (String.IsNullOrWhiteSpace accountId) then ProviderAuthRuntime.invalidate sessionStore providerId authority accountId
            member _.GetHeaders(accountId, cancellationToken) = task {
                let! session = this.GetFreshSession(accountId, cancellationToken)
                let headers = Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
                if String.IsNullOrWhiteSpace session.AccessToken then invalidOp $"{providerId} session has no access token. Sign in again."
                headers.["Authorization"] <- "Bearer " + session.AccessToken
                return headers :> IReadOnlyDictionary<string,string>
            }
          interface IProviderAuthSessionProvider with
            member _.GetSessionAsync(accountId, cancellationToken) = this.GetSessionAsync(accountId, cancellationToken) }

    member this.DiscoverModelsAsync(accountId: string, cancellationToken: CancellationToken) = task {
        let! session = this.GetFreshSession(accountId, cancellationToken)
        let! models = adapter.DiscoverModelsAsync(session, cancellationToken)
        return models |> Seq.map (fun model ->
            let copy = ProviderModelDescriptor(model.ProviderId, model.ModelId, model.ProtocolId, model.BaseUrl)
            copy.AccountId <- accountId
            copy.Authority <- authority
            copy.ModelInfo <- model.ModelInfo
            copy.SupportedEndpoints <- model.SupportedEndpoints
            copy.ReasoningEfforts <- model.ReasoningEfforts
            copy.SupportsThinkingToggle <- model.SupportsThinkingToggle
            copy.AlwaysThinking <- model.AlwaysThinking
            copy) |> Seq.toArray :> IReadOnlyList<ProviderModelDescriptor>
    }
    member this.DiscoverModelsAsync(accountId: string) = this.DiscoverModelsAsync(accountId, CancellationToken.None)

    member _.LogoutAsync(accountId: string, cancellationToken: CancellationToken) = task {
        validateAccount accountId
        let gate = ProviderAuthRuntime.gateFor sessionStore providerId authority accountId
        do! gate.WaitAsync cancellationToken
        use release = { new IDisposable with member _.Dispose() = gate.Release() |> ignore }
        do! sessionStore.DeleteAsync(providerId, authority, accountId, cancellationToken)
        ProviderAuthRuntime.clearInvalidation sessionStore providerId authority accountId
    }
    member this.LogoutAsync(accountId: string) = this.LogoutAsync(accountId, CancellationToken.None)
    interface IProviderAuthSessionProvider with
        member _.GetSessionAsync(accountId, cancellationToken) = this.GetSessionAsync(accountId, cancellationToken)
    member internal _.SessionStore = sessionStore
    member internal _.HttpClient = http
    member internal _.Clock = clock
    interface IDisposable with member _.Dispose() = if ownsHttp then http.Dispose()
