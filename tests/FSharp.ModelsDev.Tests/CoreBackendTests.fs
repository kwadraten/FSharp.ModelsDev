namespace FSharp.ModelsDev.Tests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.ModelsDev

type private FramingBackend() =
    let mutable parserNumber = 0
    let mutable requestNumber = 0
    member _.ParserNumber = parserNumber
    member _.RequestNumber = requestNumber
    interface IChatBackend with
        member _.PrepareRequest(_request, _stream, _ct) =
            task {
                do! Task.Yield()
                requestNumber <- requestNumber + 1
                let headers = Dictionary<string, string>()
                headers["X-Request"] <- string requestNumber
                return PreparedChatRequest($"https://example.test/{requestNumber}", headers, string requestNumber)
            }
        member _.ParseResponse(body) = ChatResponse(body, "fake", ChatMessage.Assistant(body), Usage(), FinishReason.Stop)
        member _.CreateStreamParser() =
            parserNumber <- parserNumber + 1
            let ownNumber = parserNumber
            let callback (_eventName: string) (data: string) =
                data.Split('\n')
                |> Array.map (fun value -> TextDelta($"{ownNumber}:{value}") :> ChatStreamEvent)
                :> IReadOnlyList<ChatStreamEvent>
            SseFrameParser(callback) :> IChatStreamParser

type private CancellingBackend(observed: TaskCompletionSource<unit>) =
    interface IChatBackend with
        member _.PrepareRequest(_request, _stream, ct) =
            task {
                ct.Register(fun () -> observed.TrySetResult(()) |> ignore) |> ignore
                do! Task.Delay(Timeout.Infinite, ct)
                return PreparedChatRequest("https://example.test", Dictionary<string, string>(), "")
            }
        member _.ParseResponse(_body) = ChatResponse("", "", ChatMessage.Assistant(""), Usage(), FinishReason.Unknown)
        member _.CreateStreamParser() = failwith "not used"

type CoreBackendTests() =

    [<Fact>]
    member _.``backend preparation is asynchronous per request and parsers are isolated with EOF framing`` () : Task =
        task {
            let backend = FramingBackend()
            let transport = FakeTransport(TransportResponse(200, "response"), [| "data: first"; "data: second"; ""; "data: final" |])
            let client = ChatClient("fake", backend :> IChatBackend, transport :> IChatTransport)
            let! first = client.Complete(ChatRequest("fake", [||]), CancellationToken.None)
            let! second = client.Complete(ChatRequest("fake", [||]), CancellationToken.None)
            Assert.Equal("response", first.Id)
            Assert.Equal("response", second.Id)
            Assert.Equal(2, backend.RequestNumber)

            let collect () = task {
                let output = ResizeArray<string>()
                let iterator = client.Stream(ChatRequest("fake", [||]), CancellationToken.None).GetAsyncEnumerator(CancellationToken.None)
                let mutable more = true
                try
                    while more do
                        let! moved = iterator.MoveNextAsync().AsTask()
                        more <- moved
                        if moved then
                            match iterator.Current with
                            | :? TextDelta as delta -> output.Add(delta.Text)
                            | _ -> ()
                finally
                    iterator.DisposeAsync() |> ignore
                return output.ToArray()
            }
            let! streamA = collect ()
            let! streamB = collect ()
            Assert.Equal<string[]>([| "1:first"; "1:second"; "1:final" |], streamA)
            Assert.Equal<string[]>([| "2:first"; "2:second"; "2:final" |], streamB)
            Assert.Equal(2, backend.ParserNumber)
        }

    [<Fact>]
    member _.``request and enumeration cancellation both reach asynchronous preparation`` () : Task =
        task {
            let requestObserved = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let requestCancellation = new CancellationTokenSource()
            let requestClient = ChatClient("fake", CancellingBackend(requestObserved) :> IChatBackend, FakeTransport(TransportResponse(200, ""), [||]) :> IChatTransport)
            let requestIterator = requestClient.Stream(ChatRequest("fake", [||]), requestCancellation.Token).GetAsyncEnumerator(CancellationToken.None)
            let requestMove = requestIterator.MoveNextAsync().AsTask()
            requestCancellation.Cancel()
            do! requestObserved.Task
            let requestAction = Func<Task>(fun () -> requestMove)
            let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(requestAction)
            do! requestIterator.DisposeAsync().AsTask()

            let enumerationObserved = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let enumerationCancellation = new CancellationTokenSource()
            let enumerationClient = ChatClient("fake", CancellingBackend(enumerationObserved) :> IChatBackend, FakeTransport(TransportResponse(200, ""), [||]) :> IChatTransport)
            let enumerationIterator = enumerationClient.Stream(ChatRequest("fake", [||]), CancellationToken.None).GetAsyncEnumerator(enumerationCancellation.Token)
            let enumerationMove = enumerationIterator.MoveNextAsync().AsTask()
            enumerationCancellation.Cancel()
            do! enumerationObserved.Task
            let enumerationAction = Func<Task>(fun () -> enumerationMove)
            let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(enumerationAction)
            do! enumerationIterator.DisposeAsync().AsTask()

            let disposeObserved = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let disposeClient = ChatClient("fake", CancellingBackend(disposeObserved) :> IChatBackend, FakeTransport(TransportResponse(200, ""), [||]) :> IChatTransport)
            let disposeIterator = disposeClient.Stream(ChatRequest("fake", [||]), CancellationToken.None).GetAsyncEnumerator(CancellationToken.None)
            do! disposeIterator.DisposeAsync().AsTask()
            Assert.True(disposeObserved.Task.IsCompleted)
        }
