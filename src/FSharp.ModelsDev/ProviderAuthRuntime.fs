namespace FSharp.ModelsDev.Providers

open System
open System.Collections.Concurrent
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks

/// Process-local provider token store. Values are copied on both sides of the store boundary.
[<Sealed>]
type InMemoryProviderAuthSessionStore() =
    let sessions = ConcurrentDictionary<string * string * string, ProviderAuthSession>()
    let copy (s: ProviderAuthSession) =
        if isNull s then null
        else
            let result = ProviderAuthSession(s.ProviderId, s.Authority, s.AccountId)
            result.CredentialKind <- s.CredentialKind
            result.AccessToken <- s.AccessToken
            result.RefreshToken <- s.RefreshToken
            result.UpstreamToken <- s.UpstreamToken
            result.ExpiresAt <- s.ExpiresAt
            result.ApiBaseUrl <- s.ApiBaseUrl
            result.DeviceId <- s.DeviceId
            result.OrganizationId <- s.OrganizationId
            result
    interface IProviderAuthSessionStore with
        member _.LoadAsync(providerId, authority, accountId, ct) =
            ct.ThrowIfCancellationRequested()
            match sessions.TryGetValue((providerId, authority, accountId)) with
            | true, value -> Task.FromResult(copy value)
            | _ -> Task.FromResult(null)
        member _.SaveAsync(session, ct) =
            ct.ThrowIfCancellationRequested()
            if isNull session then nullArg "session"
            sessions.[(session.ProviderId, session.Authority, session.AccountId)] <- copy session
            Task.CompletedTask
        member _.DeleteAsync(providerId, authority, accountId, ct) =
            ct.ThrowIfCancellationRequested()
            sessions.TryRemove((providerId, authority, accountId)) |> ignore
            Task.CompletedTask

/// Shared synchronization for refreshes: clients using the same store and authority share one gate.
module internal ProviderAuthRuntime =
    let private gates = ConditionalWeakTable<IProviderAuthSessionStore, ConcurrentDictionary<string * string * string, SemaphoreSlim>>()
    let private invalidations = ConditionalWeakTable<IProviderAuthSessionStore, ConcurrentDictionary<string * string * string, byte>>()
    let gateFor (store: IProviderAuthSessionStore) providerId authority accountId =
        let storeGates = gates.GetValue(store, fun _ -> ConcurrentDictionary<string * string * string, SemaphoreSlim>())
        storeGates.GetOrAdd((providerId, authority, accountId), fun _ -> new SemaphoreSlim(1, 1))

    let isInvalidated (store: IProviderAuthSessionStore) providerId authority accountId =
        let pending = invalidations.GetValue(store, fun _ -> ConcurrentDictionary<string * string * string, byte>())
        pending.ContainsKey((providerId, authority, accountId))

    let invalidate (store: IProviderAuthSessionStore) providerId authority accountId =
        let pending = invalidations.GetValue(store, fun _ -> ConcurrentDictionary<string * string * string, byte>())
        pending.[(providerId, authority, accountId)] <- 0uy

    let clearInvalidation (store: IProviderAuthSessionStore) providerId authority accountId =
        let pending = invalidations.GetValue(store, fun _ -> ConcurrentDictionary<string * string * string, byte>())
        pending.TryRemove((providerId, authority, accountId)) |> ignore

    let cloneSession (s: ProviderAuthSession) =
        if isNull s then null
        else
            let copy = ProviderAuthSession(s.ProviderId, s.Authority, s.AccountId)
            copy.CredentialKind <- s.CredentialKind
            copy.AccessToken <- s.AccessToken
            copy.RefreshToken <- s.RefreshToken
            copy.UpstreamToken <- s.UpstreamToken
            copy.ExpiresAt <- s.ExpiresAt
            copy.ApiBaseUrl <- s.ApiBaseUrl
            copy.DeviceId <- s.DeviceId
            copy.OrganizationId <- s.OrganizationId
            copy

    let validateAccount accountId =
        if String.IsNullOrWhiteSpace accountId then invalidArg "accountId" "A non-empty account id is required."

    let validateSession providerId authority accountId (session: ProviderAuthSession) =
        if isNull session then invalidOp $"No {providerId} credentials are stored for account '{accountId}'. Sign in first."
        if not (String.Equals(session.ProviderId, providerId, StringComparison.Ordinal))
           || not (String.Equals(session.Authority, authority, StringComparison.Ordinal))
           || not (String.Equals(session.AccountId, accountId, StringComparison.Ordinal)) then
            invalidOp "Stored provider-auth session identity did not match the requested provider, authority, and account."

    let sanitizedOAuthError (ex: exn) =
        let message = if isNull ex.Message then "" else ex.Message
        let known = [ "invalid_grant"; "access_denied"; "authorization_pending"; "slow_down"; "expired_token"; "temporarily_unavailable"; "server_error" ]
        known |> List.tryFind (fun code -> message.Contains(code, StringComparison.OrdinalIgnoreCase))

    let oauthFailureMessage providerId code =
        let safeCode =
            match code with
            | "invalid_grant" | "access_denied" | "authorization_pending" | "slow_down" | "expired_token" | "temporarily_unavailable" | "server_error" -> code
            | _ -> "authentication_failed"
        match safeCode with
        | "invalid_grant" -> $"{providerId} refresh token was rejected (invalid_grant). Sign in again."
        | "access_denied" -> $"{providerId} authorization was denied."
        | _ -> $"{providerId} authentication failed ({safeCode})."
