namespace FSharp.ModelsDev

open System
open System.Collections.Generic

/// Shared SSE framing for backends whose wire protocols attach meaning to event names.
/// It buffers a complete SSE frame before invoking the backend callback, including
/// multiple data lines and a final unterminated frame at EOF.
[<Sealed>]
type internal SseFrameParser(callback: string -> string -> IReadOnlyList<ChatStreamEvent>) =
    let data = ResizeArray<string>()
    let mutable eventName: string = null

    let empty () = Array.empty<ChatStreamEvent> :> IReadOnlyList<ChatStreamEvent>

    let dispatch () =
        if data.Count = 0 then
            eventName <- null
            empty ()
        else
            let payload = String.Join("\n", data)
            let name = eventName
            data.Clear()
            eventName <- null
            let events = callback name payload
            if isNull events then empty () else events

    member _.PushLine(line: string) : IReadOnlyList<ChatStreamEvent> =
        if isNull line then
            empty ()
        elif line.Length = 0 then
            dispatch ()
        elif line.StartsWith(":", StringComparison.Ordinal) then
            empty ()
        else
            let separator = line.IndexOf(':')
            let field, value =
                if separator < 0 then line, ""
                else
                    let rawValue = line.Substring(separator + 1)
                    line.Substring(0, separator),
                    (if rawValue.StartsWith(" ", StringComparison.Ordinal) then rawValue.Substring(1) else rawValue)
            match field with
            | "data" -> data.Add(value); empty ()
            | "event" -> eventName <- value; empty ()
            | _ -> empty ()

    member _.Finish() : IReadOnlyList<ChatStreamEvent> = dispatch ()

    interface IChatStreamParser with
        member this.PushLine(line) = this.PushLine(line)
        member this.Finish() = this.Finish()
