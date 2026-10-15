namespace FSharp.ModelsDev.Providers

open System
open System.Collections.Generic
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog

/// Static API-key credentials; emits a single header, ignoring the account.
[<Sealed>]
type ApiKeyCredentialProvider(apiKey: string, headerName: string, scheme: string) =
    do
        if isNull apiKey then
            nullArg "apiKey"

    let header = if isNull headerName then "Authorization" else headerName

    let value =
        match if isNull scheme then "Bearer" else scheme with
        | null
        | "" -> apiKey
        | scheme -> scheme + " " + apiKey

    new(apiKey: string) = ApiKeyCredentialProvider(apiKey, null, null)

    interface ICredentialProvider with
        member _.GetHeaders(_accountId: string, _cancellationToken: CancellationToken): Task<IReadOnlyDictionary<string, string>> =
            let headers = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            headers.[header] <- value
            Task.FromResult(headers :> IReadOnlyDictionary<string, string>)

/// Reads the first set environment variable listed by the provider definition; ignores the account.
[<Sealed>]
type EnvCredentialProvider(provider: ProviderDefinition) =
    do
        if isNull (box provider) then
            nullArg "provider"

    interface ICredentialProvider with
        member _.GetHeaders(_accountId: string, _cancellationToken: CancellationToken): Task<IReadOnlyDictionary<string, string>> =
            let rec find (index: int) =
                if isNull provider.EnvVars || index >= provider.EnvVars.Count then
                    None
                else
                    match Environment.GetEnvironmentVariable(provider.EnvVars.[index]) with
                    | null
                    | "" -> find (index + 1)
                    | value -> Some value

            match find 0 with
            | Some key ->
                let headers = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                headers.["Authorization"] <- "Bearer " + key
                Task.FromResult(headers :> IReadOnlyDictionary<string, string>)
            | None ->
                let names =
                    if isNull provider.EnvVars then
                        ""
                    else
                        String.Join(", ", provider.EnvVars)

                raise (
                    AiException(
                        AiError.Transport $"no API key found for provider '{provider.ProviderId}'; set one of the environment variables: {names}"
                    )
                )
