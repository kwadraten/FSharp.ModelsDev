namespace FSharp.ModelsDev.Tests

open System
open Xunit
open FSharp.ModelsDev
open FSharp.ModelsDev.Catalog
open FSharp.ModelsDev.Providers

type CapabilityPolicyTests() =

    let reasoningModel =
        ModelInfo(
            "openai",
            "gpt-x",
            "GPT X",
            "openai/gpt-x",
            ModelCapabilities.TextInput ||| ModelCapabilities.ToolCalling ||| ModelCapabilities.Reasoning,
            Nullable 128000L,
            Nullable 16000L,
            [| "low"; "high" |]
        )

    let plainModel =
        ModelInfo(
            "openai",
            "gpt-p",
            "GPT P",
            "openai/gpt-p",
            ModelCapabilities.TextInput,
            Nullable 128000L,
            Nullable 16000L,
            Array.empty<string>
        )

    let requestWith (effort: string) =
        let request = ChatRequest("gpt-x", [| ChatMessage.User "hi" |])
        request.ReasoningEffort <- effort
        request

    [<Fact>]
    member _.``missing effort is supported`` () =
        let request = ChatRequest("gpt-x", [| ChatMessage.User "hi" |])
        let result = CapabilityPolicy.Resolve(reasoningModel, request, CapabilityMode.Strict)

        Assert.Equal("supported", result.Status)
        Assert.True(result.IsUsable)
        Assert.Null(result.AdjustedEffort)
        Assert.Null(result.Reason)

    [<Fact>]
    member _.``listed effort is supported in every mode`` () =
        for mode in [| CapabilityMode.Strict; CapabilityMode.Compat; CapabilityMode.Loose |] do
            let result = CapabilityPolicy.Resolve(reasoningModel, requestWith "high", mode)

            Assert.Equal("supported", result.Status)
            Assert.True(result.IsUsable)
            Assert.Null(result.AdjustedEffort)

    [<Fact>]
    member _.``unlisted effort is rejected in strict mode`` () =
        let result = CapabilityPolicy.Resolve(reasoningModel, requestWith "medium", CapabilityMode.Strict)

        Assert.Equal("unsupported", result.Status)
        Assert.False(result.IsUsable)
        Assert.False(String.IsNullOrWhiteSpace(result.Reason))

    [<Fact>]
    member _.``unlisted effort is downgraded in compat mode`` () =
        let result = CapabilityPolicy.Resolve(reasoningModel, requestWith "medium", CapabilityMode.Compat)

        Assert.Equal("downgraded", result.Status)
        Assert.True(result.IsUsable)
        Assert.Equal("low", result.AdjustedEffort)

    [<Fact>]
    member _.``unlisted effort is allowed in loose mode`` () =
        let result = CapabilityPolicy.Resolve(reasoningModel, requestWith "medium", CapabilityMode.Loose)

        Assert.Equal("supported", result.Status)
        Assert.True(result.IsUsable)
        Assert.Null(result.AdjustedEffort)

    [<Fact>]
    member _.``effort on non reasoning model strict is unsupported`` () =
        let result = CapabilityPolicy.Resolve(plainModel, requestWith "low", CapabilityMode.Strict)

        Assert.Equal("unsupported", result.Status)
        Assert.False(result.IsUsable)

    [<Fact>]
    member _.``effort on non reasoning model loose is supported`` () =
        let result = CapabilityPolicy.Resolve(plainModel, requestWith "low", CapabilityMode.Loose)

        Assert.Equal("supported", result.Status)
        Assert.True(result.IsUsable)

    [<Fact>]
    member _.``effort on non reasoning model compat drops effort`` () =
        let result = CapabilityPolicy.Resolve(plainModel, requestWith "low", CapabilityMode.Compat)

        Assert.True(result.IsUsable)
        Assert.Null(result.AdjustedEffort)
