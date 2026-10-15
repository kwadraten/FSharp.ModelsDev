namespace FSharp.ModelsDev

open System
open System.Collections.Generic
open System.Runtime.InteropServices

type Role =
    | System = 0
    | User = 1
    | Assistant = 2
    | Tool = 3

[<AbstractClass; AllowNullLiteral>]
type ContentPart() =
    abstract Kind: string

[<Sealed; AllowNullLiteral>]
type TextPart(text: string) =
    inherit ContentPart()
    member _.Text = text
    override _.Kind = "text"

[<Sealed; AllowNullLiteral>]
type ImagePart(url: string, mediaType: string) =
    inherit ContentPart()
    new(url: string) = ImagePart(url, null)
    member _.Url = url
    member _.MediaType = mediaType
    override _.Kind = "image"

[<Sealed; AllowNullLiteral>]
type ToolCall(id: string, name: string, argumentsJson: string) =
    member _.Id = id
    member _.Name = name
    member _.ArgumentsJson = argumentsJson

[<Sealed; AllowNullLiteral>]
type ToolDefinition(name: string, description: string, parametersJson: string) =
    member _.Name = name
    member _.Description = description
    member _.ParametersJson = parametersJson

[<Sealed; AllowNullLiteral>]
type ChatMessage
    (
        role: Role,
        parts: IReadOnlyList<ContentPart>,
        name: string,
        toolCalls: IReadOnlyList<ToolCall>,
        toolCallId: string
    ) =
    new(role: Role, parts: IReadOnlyList<ContentPart>) = ChatMessage(role, parts, null, null, null)
    member _.Role = role
    member _.Parts = parts
    member _.Name = name
    member _.ToolCalls = toolCalls
    member _.ToolCallId = toolCallId

    member _.Text =
        parts
        |> Seq.choose (function
            | :? TextPart as t -> Some t.Text
            | _ -> None)
        |> String.concat ""

    static member System(text: string) = ChatMessage(Role.System, [| TextPart text |])
    static member User(text: string) = ChatMessage(Role.User, [| TextPart text |])
    static member Assistant(text: string) = ChatMessage(Role.Assistant, [| TextPart text |])

    static member ToolResult(toolCallId: string, text: string) =
        ChatMessage(Role.Tool, [| TextPart text |], null, null, toolCallId)

type FinishReason =
    | Unknown = 0
    | Stop = 1
    | Length = 2
    | ToolCalls = 3
    | ContentFilter = 4

[<Sealed; AllowNullLiteral>]
type Usage(inputTokens: int64, outputTokens: int64, totalTokens: int64) =
    new() = Usage(0L, 0L, 0L)
    member _.InputTokens = inputTokens
    member _.OutputTokens = outputTokens
    member _.TotalTokens = totalTokens

[<Sealed; AllowNullLiteral>]
type ChatRequest(model: string, messages: IReadOnlyList<ChatMessage>) =
    member _.Model = model
    member _.Messages = messages
    member val Temperature = Nullable<float>() with get, set
    member val MaxTokens = Nullable<int>() with get, set
    member val TopP = Nullable<float>() with get, set
    member val Stream = false with get, set
    member val Tools: IReadOnlyList<ToolDefinition> = null with get, set
    member val ReasoningEffort: string = null with get, set
    member val StopSequences: IReadOnlyList<string> = null with get, set

[<Sealed; AllowNullLiteral>]
type ChatResponse(id: string, model: string, message: ChatMessage, usage: Usage, finishReason: FinishReason) =
    member _.Id = id
    member _.Model = model
    member _.Message = message
    member _.Usage = usage
    member _.FinishReason = finishReason

[<AbstractClass; AllowNullLiteral>]
type ChatStreamEvent() =
    abstract Kind: string

[<Sealed; AllowNullLiteral>]
type TextDelta(text: string) =
    inherit ChatStreamEvent()
    member _.Text = text
    override _.Kind = "text-delta"

[<Sealed; AllowNullLiteral>]
type ToolCallDelta(index: int, id: string, name: string, argumentsDelta: string) =
    inherit ChatStreamEvent()
    member _.Index = index
    member _.Id = id
    member _.Name = name
    member _.ArgumentsDelta = argumentsDelta
    override _.Kind = "tool-call-delta"

[<Sealed; AllowNullLiteral>]
type UsageUpdate(usage: Usage) =
    inherit ChatStreamEvent()
    member _.Usage = usage
    override _.Kind = "usage"

[<Sealed; AllowNullLiteral>]
type Completed(finishReason: FinishReason, usage: Usage) =
    inherit ChatStreamEvent()
    member _.FinishReason = finishReason
    member _.Usage = usage
    override _.Kind = "completed"
