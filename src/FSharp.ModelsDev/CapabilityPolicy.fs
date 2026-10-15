namespace FSharp.ModelsDev.Providers

open System
open System.Collections.Generic
open System.Runtime.InteropServices
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog

type internal Resolution =
    | Supported
    | Downgraded of adjustedEffort: string
    | Unsupported of reason: string
    | InvalidCombination of reason: string

/// How strictly a request is matched against model capabilities.
type CapabilityMode =
    | Strict = 0
    | Compat = 1
    | Loose = 2

/// Outcome of checking a request against a model's declared capabilities.
[<Sealed>]
type CapabilityResolution internal (resolution: Resolution) =

    /// One of "supported", "downgraded", "unsupported", "invalid-combination".
    member _.Status =
        match resolution with
        | Supported -> "supported"
        | Downgraded _ -> "downgraded"
        | Unsupported _ -> "unsupported"
        | InvalidCombination _ -> "invalid-combination"

    /// Human-readable explanation for non-supported outcomes; null otherwise.
    member _.Reason =
        match resolution with
        | Unsupported reason -> reason
        | InvalidCombination reason -> reason
        | _ -> null

    /// Effort level the request should be adjusted to; null when the parameter is dropped or unchanged.
    member _.AdjustedEffort =
        match resolution with
        | Downgraded adjusted -> adjusted
        | _ -> null

    /// True when the request can proceed (possibly with adjustments).
    member _.IsUsable =
        match resolution with
        | Supported
        | Downgraded _ -> true
        | _ -> false

/// Resolves a chat request against a model's reasoning capability.
[<AbstractClass; Sealed>]
type CapabilityPolicy private () =

    static member private EffortRank(level: string): int =
        match level with
        | null -> -1
        | s when s.Equals("low", StringComparison.OrdinalIgnoreCase) -> 0
        | s when s.Equals("medium", StringComparison.OrdinalIgnoreCase) -> 1
        | s when s.Equals("high", StringComparison.OrdinalIgnoreCase) -> 2
        | _ -> 3

    static member private NearestEffort(requested: string, efforts: IReadOnlyList<string>): string =
        if isNull efforts || efforts.Count = 0 then
            null
        else
            let requestedRank = CapabilityPolicy.EffortRank requested

            let atOrBelow =
                [ for effort in efforts do
                      if CapabilityPolicy.EffortRank effort <= requestedRank then
                          yield effort ]

            match atOrBelow with
            | [] -> efforts |> Seq.minBy CapabilityPolicy.EffortRank
            | candidates -> candidates |> List.maxBy CapabilityPolicy.EffortRank

    static member private ResolveInternal(model: ModelInfo, request: ChatRequest, mode: CapabilityMode): CapabilityResolution =
        match request.ReasoningEffort with
        | null -> CapabilityResolution Supported
        | requested ->
            if not model.SupportsReasoning then
                match mode with
                | CapabilityMode.Strict ->
                    CapabilityResolution(Unsupported $"model '{model.ModelId}' does not support reasoning")
                | CapabilityMode.Compat -> CapabilityResolution(Downgraded null)
                | _ -> CapabilityResolution Supported
            else
                let efforts = model.ReasoningEfforts

                let containsRequested =
                    not (isNull efforts)
                    && efforts
                       |> Seq.exists (fun effort -> String.Equals(effort, requested, StringComparison.OrdinalIgnoreCase))

                if containsRequested then
                    CapabilityResolution Supported
                else
                    match mode with
                    | CapabilityMode.Strict ->
                        let supported =
                            if isNull efforts then
                                ""
                            else
                                String.Join(", ", efforts)

                        CapabilityResolution(
                            Unsupported
                                $"model '{model.ModelId}' does not support reasoning effort '{requested}' (supported: {supported})"
                        )
                    | CapabilityMode.Compat ->
                        CapabilityResolution(Downgraded(CapabilityPolicy.NearestEffort(requested, efforts)))
                    | _ -> CapabilityResolution Supported

    /// Resolves the request with Compat semantics.
    static member Resolve(model: ModelInfo, request: ChatRequest): CapabilityResolution =
        CapabilityPolicy.ResolveInternal(model, request, CapabilityMode.Compat)

    /// Resolves the request with the given mode.
    static member Resolve(model: ModelInfo, request: ChatRequest, mode: CapabilityMode): CapabilityResolution =
        CapabilityPolicy.ResolveInternal(model, request, mode)
