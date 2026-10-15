namespace FSharp.ModelsDev

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Text
open System.Runtime.ExceptionServices
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open FSharp.ModelsDev.Providers

module internal ExceptionUtil =
    let rethrowAs<'T> (e: exn) : 'T =
        ExceptionDispatchInfo.Capture(e).Throw()
        Unchecked.defaultof<'T>

/// Runs a producer on a hot background task and exposes its output as an IAsyncEnumerable.
[<Sealed>]
type private ChannelAsyncEnumerable<'T>(produce: ChannelWriter<'T> -> CancellationToken -> Task<unit>) =
    interface IAsyncEnumerable<'T> with
        member _.GetAsyncEnumerator(ct) =
            let cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
            let channel = Channel.CreateUnbounded<'T>(UnboundedChannelOptions(SingleReader = true))
            let writer = channel.Writer
            let work: Task<unit> =
                task {
                    try
                        do! produce writer cts.Token
                        writer.Complete()
                    with
                    | :? OperationCanceledException when cts.IsCancellationRequested -> writer.Complete()
                    | e -> writer.TryComplete(e) |> ignore
                }
            ignore work
            let inner = channel.Reader.ReadAllAsync(cts.Token).GetAsyncEnumerator()
            { new IAsyncEnumerator<'T> with
                member _.Current = inner.Current
                member _.MoveNextAsync() = inner.MoveNextAsync()
                member _.DisposeAsync() =
                    cts.Cancel()
                    inner.DisposeAsync() }

module internal AsyncEnumerableBridge =
    let ofProducer (produce: ChannelWriter<'T> -> CancellationToken -> Task<unit>) : IAsyncEnumerable<'T> =
        ChannelAsyncEnumerable<'T>(produce) :> IAsyncEnumerable<'T>

/// Default IChatTransport over HTTP, sharing one static HttpClient instance.
[<Sealed>]
type HttpChatTransport() =

    static let client = new HttpClient()

    static let isSuccess (code: int) = code >= 200 && code <= 299

    let buildRequest (url: string) (headers: IReadOnlyDictionary<string, string>) (body: string) =
        let msg = new HttpRequestMessage(HttpMethod.Post, url)
        msg.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        for KeyValue(k, v) in headers do
            msg.Headers.TryAddWithoutValidation(k, v) |> ignore
        msg

    /// Sends a JSON POST and returns the full response; throws AiException on non-2xx.
    member _.Post(url: string, headers: IReadOnlyDictionary<string, string>, body: string, cancellationToken: CancellationToken) : Task<TransportResponse> =
        task {
            use msg = buildRequest url headers body
            try
                use! resp = client.SendAsync(msg, cancellationToken)
                let! text = resp.Content.ReadAsStringAsync(cancellationToken)
                let code = int resp.StatusCode
                if isSuccess code then
                    return TransportResponse(code, text)
                else
                    return raise (AiException(AiError.Http(code, text)))
            with e ->
                match e with
                | :? OperationCanceledException -> return ExceptionUtil.rethrowAs e
                | :? AiException -> return ExceptionUtil.rethrowAs e
                | _ -> return raise (AiException(AiError.Transport e.Message, e))
        }

    /// Sends a JSON POST accepting text/event-stream and yields the raw SSE lines.
    member _.PostStream(url: string, headers: IReadOnlyDictionary<string, string>, body: string, cancellationToken: CancellationToken) : IAsyncEnumerable<string> =
        let produce (writer: ChannelWriter<string>) (ct: CancellationToken) : Task<unit> =
            task {
                use msg = buildRequest url headers body
                msg.Headers.TryAddWithoutValidation("Accept", "text/event-stream") |> ignore
                use! resp = client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct)
                let code = int resp.StatusCode
                if not (isSuccess code) then
                    let! text = resp.Content.ReadAsStringAsync(ct)
                    return raise (AiException(AiError.Http(code, text)))
                use reader = new StreamReader(resp.Content.ReadAsStream(ct))
                use enumerator = (Sse.readLines reader ct).GetAsyncEnumerator(ct)
                let mutable more = true
                while more do
                    let! hasNext = enumerator.MoveNextAsync().AsTask()
                    more <- hasNext
                    if hasNext then
                        do! writer.WriteAsync(enumerator.Current, ct).AsTask()
            }
        AsyncEnumerableBridge.ofProducer produce

    interface IChatTransport with
        member this.Post(url, headers, body, cancellationToken) = this.Post(url, headers, body, cancellationToken)
        member this.PostStream(url, headers, body, cancellationToken) = this.PostStream(url, headers, body, cancellationToken)

/// Standalone chat model: IChatProtocol wire format over an IChatTransport endpoint.
[<Sealed>]
type ChatClient(model: string, protocol: IChatProtocol, transport: IChatTransport, url: string, headers: IReadOnlyDictionary<string, string>) =

    /// Model id sent with every request.
    member _.Model = model

    /// Runs a non-streaming completion; a Stream flag on the request is treated as false.
    member _.Complete(request: ChatRequest, cancellationToken: CancellationToken) : Task<ChatResponse> =
        task {
            let body = protocol.SerializeRequest(request, false)
            let! response = transport.Post(url, headers, body, cancellationToken)
            if response.StatusCode >= 200 && response.StatusCode <= 299 then
                return protocol.ParseResponse(response.Body)
            else
                return raise (AiException(AiError.Http(response.StatusCode, response.Body)))
        }

    /// Streams parsed ChatStreamEvents, skipping lines the protocol maps to null.
    member _.Stream(request: ChatRequest, cancellationToken: CancellationToken) : IAsyncEnumerable<ChatStreamEvent> =
        let body = protocol.SerializeRequest(request, true)
        let lines = transport.PostStream(url, headers, body, cancellationToken)
        let produce (writer: ChannelWriter<ChatStreamEvent>) (ct: CancellationToken) : Task<unit> =
            task {
                use enumerator = lines.GetAsyncEnumerator(ct)
                let mutable more = true
                while more do
                    let! hasNext = enumerator.MoveNextAsync().AsTask()
                    more <- hasNext
                    if hasNext then
                        let evt = protocol.TryParseStreamLine(enumerator.Current)
                        if not (Object.ReferenceEquals(evt, null)) then
                            do! writer.WriteAsync(evt, ct).AsTask()
            }
        AsyncEnumerableBridge.ofProducer produce

    interface IChatModel with
        member _.Model = model
        member this.Complete(request, cancellationToken) = this.Complete(request, cancellationToken)
        member this.Stream(request, cancellationToken) = this.Stream(request, cancellationToken)

    /// Creates a ChatClient for the OpenAI /chat/completions endpoint.
    static member OpenAI(apiKey: string, model: string) : ChatClient =
        ChatClient.OpenAI(apiKey, model, null, null)

    /// Creates a ChatClient for an OpenAI-compatible /chat/completions endpoint.
    static member OpenAI(apiKey: string, model: string, baseUrl: string, transport: IChatTransport) : ChatClient =
        let baseUrl =
            if String.IsNullOrEmpty baseUrl then
                "https://api.openai.com/v1"
            else
                baseUrl

        let url = baseUrl.TrimEnd('/') + "/chat/completions"
        let headers = Dictionary<string, string>()
        headers["Authorization"] <- $"Bearer {apiKey}"
        let protocol = OpenAICompatibleChatProtocol() :> IChatProtocol

        let transport =
            if isNull transport then
                HttpChatTransport() :> IChatTransport
            else
                transport

        ChatClient(model, protocol, transport, url, headers)
