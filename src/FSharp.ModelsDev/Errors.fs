namespace FSharp.ModelsDev

open System
open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("FSharp.ModelsDev.Tests")>]
do ()

type internal AiError =
    | Http of statusCode: int * body: string
    | Decode of message: string
    | Transport of message: string

    member this.Message =
        match this with
        | Http(code, body) -> $"HTTP {code}: {body}"
        | Decode msg -> $"Decode error: {msg}"
        | Transport msg -> $"Transport error: {msg}"

type AiException internal (error: AiError, inner: exn) =
    inherit Exception(error.Message, inner)

    member internal _.Error = error

    member _.StatusCode =
        match error with
        | Http(code, _) -> Nullable code
        | _ -> Nullable()

    new(message: string) = AiException(Transport message, null)
    new(message: string, statusCode: int) = AiException(Http(statusCode, message), null)
    internal new(error: AiError) = AiException(error, null)
