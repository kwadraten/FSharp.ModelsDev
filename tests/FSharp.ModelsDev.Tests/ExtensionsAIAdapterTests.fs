namespace FSharp.ModelsDev.Tests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.ModelsDev
open FSharp.ModelsDev.MEAi

type MeaIChatClient = Microsoft.Extensions.AI.IChatClient
type MeaChatMessage = Microsoft.Extensions.AI.ChatMessage
type MeaChatRole = Microsoft.Extensions.AI.ChatRole
type MeaChatResponseUpdate = Microsoft.Extensions.AI.ChatResponseUpdate

/// IChatModel stub returning a fixed response and a fixed event sequence.
type FakeChatModel(responseText: string, events: ChatStreamEvent[]) =
    interface IChatModel with
        member _.Model = "fake-model"

        member _.Complete(_request, _ct) : Task<ChatResponse> =
            Task.FromResult(
                ChatResponse(
                    "resp-1",
                    "fake-model",
                    ChatMessage.Assistant(responseText),
                    Usage(1L, 2L, 3L),
                    FinishReason.Stop
                )
            )

        member _.Stream(_request, _ct) : IAsyncEnumerable<ChatStreamEvent> =
            SeqAsyncEnumerable<ChatStreamEvent>(events) :> IAsyncEnumerable<ChatStreamEvent>

type ExtensionsAIAdapterTests() =

    [<Fact>]
    member _.``AsIChatClient returns a Microsoft Extensions AI client`` () =
        let model = FakeChatModel("hello", Array.empty<ChatStreamEvent>) :> IChatModel
        let client = model.AsIChatClient()

        Assert.IsAssignableFrom<MeaIChatClient>(client)

    [<Fact>]
    member _.``GetResponseAsync returns model text`` () : Task =
        task {
            let model = FakeChatModel("hello from fake model", Array.empty<ChatStreamEvent>) :> IChatModel
            let client = model.AsIChatClient()
            let messages = [| MeaChatMessage(MeaChatRole.User, "hi") |]

            let! response =
                client.GetResponseAsync(messages, null, CancellationToken.None)

            Assert.Equal("hello from fake model", response.Text)
        }

    [<Fact>]
    member _.``GetStreamingResponseAsync yields model events`` () : Task =
        task {
            let events =
                [| TextDelta "he" :> ChatStreamEvent
                   TextDelta "llo" :> ChatStreamEvent |]

            let model = FakeChatModel("unused", events) :> IChatModel
            let client = model.AsIChatClient()
            let updates = ResizeArray<MeaChatResponseUpdate>()

            let enumerator =
                client
                    .GetStreamingResponseAsync(
                        [| MeaChatMessage(MeaChatRole.User, "hi") |],
                        null,
                        CancellationToken.None
                    )
                    .GetAsyncEnumerator(CancellationToken.None)

            let mutable more = true

            try
                while more do
                    let! moved = enumerator.MoveNextAsync().AsTask()
                    more <- moved

                    if moved then
                        updates.Add(enumerator.Current)
            finally
                enumerator.DisposeAsync() |> ignore

            Assert.Equal(2, updates.Count)

            let text =
                updates |> Seq.map (fun update -> update.Text) |> String.concat ""

            Assert.Equal("hello", text)
        }
