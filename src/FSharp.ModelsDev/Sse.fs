namespace FSharp.ModelsDev.Providers

open System.IO
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic

/// Reads raw SSE stream lines; parsing of `data:`/`event:` payloads is left to the protocol codec.
module internal Sse =

    type private LineEnumerator(reader: StreamReader, token: CancellationToken, linked: CancellationTokenSource) =
        let mutable current: string = null

        interface IAsyncEnumerator<string> with
            member _.Current = current

            member _.MoveNextAsync() =
                let read = reader.ReadLineAsync(token)
                if read.IsCompletedSuccessfully then
                    let line = read.Result
                    current <- line
                    ValueTask<bool>(line <> null)
                else
                    let awaitNext () =
                        task {
                            let! (line: string) = read.AsTask()
                            current <- line
                            return line <> null
                        }

                    ValueTask<bool>(awaitNext ())

            member _.DisposeAsync() =
                (if not (isNull linked) then linked.Dispose())
                ValueTask()

    type private LineEnumerable(reader: StreamReader, cancellationToken: CancellationToken) =
        interface IAsyncEnumerable<string> with
            member _.GetAsyncEnumerator(consumerToken) =
                let linked, token =
                    if consumerToken.CanBeCanceled
                       && cancellationToken.CanBeCanceled
                       && consumerToken <> cancellationToken then
                        let cts =
                            CancellationTokenSource.CreateLinkedTokenSource(consumerToken, cancellationToken)

                        cts, cts.Token
                    elif consumerToken.CanBeCanceled then
                        null, consumerToken
                    else
                        null, cancellationToken

                LineEnumerator(reader, token, linked) :> IAsyncEnumerator<string>

    /// Yields each raw line of the reader until end of stream.
    let readLines (reader: StreamReader) (cancellationToken: CancellationToken) : IAsyncEnumerable<string> =
        LineEnumerable(reader, cancellationToken) :> IAsyncEnumerable<string>
