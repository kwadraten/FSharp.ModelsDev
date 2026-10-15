namespace FSharp.ModelsDev.Tests

open System
open Xunit
open FSharp.ModelsDev

type CoreTests() =

    [<Fact>]
    member _.``message factories set role and text`` () =
        let system = ChatMessage.System "be kind"
        Assert.Equal(Role.System, system.Role)
        Assert.Equal("be kind", system.Text)

        let user = ChatMessage.User "hello"
        Assert.Equal(Role.User, user.Role)
        Assert.Equal("hello", user.Text)

        let assistant = ChatMessage.Assistant "hi there"
        Assert.Equal(Role.Assistant, assistant.Role)
        Assert.Equal("hi there", assistant.Text)

        let tool = ChatMessage.ToolResult("call_9", "tool output")
        Assert.Equal(Role.Tool, tool.Role)
        Assert.Equal("call_9", tool.ToolCallId)
        Assert.Equal("tool output", tool.Text)

    [<Fact>]
    member _.``text concatenates text parts only`` () =
        let parts =
            [| TextPart "a" :> ContentPart
               ImagePart("http://example.test/img.png") :> ContentPart
               TextPart "b" :> ContentPart |]

        let message = ChatMessage(Role.User, parts)
        Assert.Equal("ab", message.Text)

    [<Fact>]
    member _.``request has expected defaults and mutable options`` () =
        let request = ChatRequest("gpt-5", [| ChatMessage.User "hi" |])

        Assert.False(request.Stream)
        Assert.False(request.Temperature.HasValue)
        Assert.False(request.MaxTokens.HasValue)
        Assert.False(request.TopP.HasValue)
        Assert.Null(request.Tools)
        Assert.Null(request.ReasoningEffort)
        Assert.Null(request.StopSequences)

        request.Stream <- true
        request.Temperature <- Nullable 0.5
        request.MaxTokens <- Nullable 128
        request.TopP <- Nullable 0.9
        request.Tools <- [| ToolDefinition("get_weather", "Get weather", "{}") |]
        request.ReasoningEffort <- "low"
        request.StopSequences <- [| "END" |]

        Assert.True(request.Stream)
        Assert.Equal(Nullable 0.5, request.Temperature)
        Assert.Equal(Nullable 128, request.MaxTokens)
        Assert.Equal(Nullable 0.9, request.TopP)
        Assert.Equal(1, request.Tools.Count)
        Assert.Equal("get_weather", request.Tools.[0].Name)
        Assert.Equal("low", request.ReasoningEffort)
        Assert.Equal(1, request.StopSequences.Count)
        Assert.Equal("END", request.StopSequences.[0])

    [<Fact>]
    member _.``usage defaults to zero`` () =
        let usage = Usage()

        Assert.Equal(0L, usage.InputTokens)
        Assert.Equal(0L, usage.OutputTokens)
        Assert.Equal(0L, usage.TotalTokens)

    [<Fact>]
    member _.``stream events expose stable kind values`` () =
        Assert.Equal("text-delta", TextDelta("x").Kind)

        let toolDelta = ToolCallDelta(0, "id", "name", "{}")
        Assert.Equal("tool-call-delta", toolDelta.Kind)
        Assert.Equal(0, toolDelta.Index)
        Assert.Equal("id", toolDelta.Id)
        Assert.Equal("name", toolDelta.Name)
        Assert.Equal("{}", toolDelta.ArgumentsDelta)

        let usageUpdate = UsageUpdate(Usage(1L, 2L, 3L))
        Assert.Equal("usage", usageUpdate.Kind)
        Assert.Equal(2L, usageUpdate.Usage.OutputTokens)

        let completed = Completed(FinishReason.Stop, Usage())
        Assert.Equal("completed", completed.Kind)
        Assert.Equal(FinishReason.Stop, completed.FinishReason)

    [<Fact>]
    member _.``AiException carries http error details`` () =
        let error = AiError.Http(429, "too many requests")
        let ex = AiException(error)

        Assert.Equal(error, ex.Error)
        Assert.Equal("HTTP 429: too many requests", ex.Message)
        Assert.True(ex.StatusCode.HasValue)
        Assert.Equal(429, ex.StatusCode.Value)

        let decodeEx = AiException(AiError.Decode "bad json")
        Assert.False(decodeEx.StatusCode.HasValue)

        let plainEx = AiException("connection lost")
        Assert.Equal("Transport error: connection lost", plainEx.Message)
