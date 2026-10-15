namespace FSharp.ModelsDev.Catalog

open System.Collections.Generic
open System.Runtime.InteropServices

/// Catalog seam: a pure, offline query surface over the compiled-in snapshot.
/// Knows nothing about accounts, credentials, or HTTP.
[<AllowNullLiteral>]
type IModelCatalog =
    abstract SnapshotVersion: string
    abstract ProviderIds: IReadOnlyList<string>

    abstract TryGetProvider:
        providerId: string * [<Out>] provider: byref<ProviderDefinition> -> bool

    abstract TryGet:
        providerId: string * modelId: string * [<Out>] model: byref<ModelInfo> -> bool

    abstract GetModels: providerId: string -> IReadOnlyList<ModelInfo>
