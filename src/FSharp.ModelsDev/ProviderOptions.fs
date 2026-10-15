namespace FSharp.ModelsDev

open System.Collections.Generic
open FSharp.ModelsDev.Providers

[<Sealed; AllowNullLiteral>]
type ProviderOptions() =
    member val ProtocolId: string = null with get, set
    member val BaseUrl: string = null with get, set
    member val ProjectId: string = null with get, set
    member val Location: string = null with get, set
    member val Credentials: ICredentialProvider = null with get, set
    member val Headers: IReadOnlyDictionary<string, string> = null with get, set
    /// Explicit account-scoped model discovery result; never fetched implicitly.
    member val DiscoveredModel: ProviderModelDescriptor = null with get, set
    /// Host session identity used by providers supporting session-scoped prompt caching.
    member val SessionId: string = null with get, set
