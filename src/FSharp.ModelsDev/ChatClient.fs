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
type private ChannelAsyncEnumerable<'T>(produce: ChannelWriter<'T> -> CancellationToken -> Task<unit>, sourceToken: CancellationToken) =
    interface IAsyncEnumerable<'T> with
        member _.GetAsyncEnumerator(ct) =
            let cts =
                if sourceToken.CanBeCanceled && ct.CanBeCanceled && sourceToken <> ct then
                    CancellationTokenSource.CreateLinkedTokenSource(ct, sourceToken)
                elif sourceToken.CanBeCanceled then CancellationTokenSource.CreateLinkedTokenSource(sourceToken)
                elif ct.CanBeCanceled then CancellationTokenSource.CreateLinkedTokenSource(ct)
                else new CancellationTokenSource()
            let channel = Channel.CreateUnbounded<'T>(UnboundedChannelOptions(SingleReader = true))
            let writer = channel.Writer
            let work: Task<unit> =
                task {
                    try
                        do! produce writer cts.Token
                        writer.Complete()
                    with e -> writer.TryComplete(e) |> ignore
                }
            ignore work
            let inner = channel.Reader.ReadAllAsync(cts.Token).GetAsyncEnumerator()
            { new IAsyncEnumerator<'T> with
                member _.Current = inner.Current
                member _.MoveNextAsync() = inner.MoveNextAsync()
                member _.DisposeAsync() =
                    cts.Cancel()
                    let cleanup = task {
                        let disposeResult = task {
                            try
                                do! inner.DisposeAsync().AsTask()
                                return None
                            with e -> return Some e
                        }
                        let producerResult = task {
                            try
                                do! work
                                return None
                            with
                            | :? OperationCanceledException when cts.IsCancellationRequested -> return None
                            | e -> return Some e
                        }
                        let! disposeError = disposeResult
                        let! producerError = producerResult
                        cts.Dispose()
                        match disposeError, producerError with
                        | Some error, _ -> return raise error
                        | _, Some error -> return raise error
                        | _ -> return ()
                    }
                    ValueTask(cleanup :> Task) }

module internal AsyncEnumerableBridge =
    let ofProducer (produce: ChannelWriter<'T> -> CancellationToken -> Task<unit>) : IAsyncEnumerable<'T> =
        ChannelAsyncEnumerable<'T>(produce, CancellationToken.None) :> IAsyncEnumerable<'T>

    let ofProducerWithCancellation (sourceToken: CancellationToken) (produce: ChannelWriter<'T> -> CancellationToken -> Task<unit>) : IAsyncEnumerable<'T> =
        ChannelAsyncEnumerable<'T>(produce, sourceToken) :> IAsyncEnumerable<'T>

/// HTTP transport. The default constructor shares an HttpClient; injected clients remain caller-owned.
[<Sealed>]
type HttpChatTransport(client: HttpClient) =

    static let sharedClient = new HttpClient()

    do if isNull client then nullArg "client"

    static let isSuccess (code: int) = code >= 200 && code <= 299

    let buildRequest (url: string) (headers: IReadOnlyDictionary<string, string>) (body: string) =
        let msg = new HttpRequestMessage(HttpMethod.Post, url)
        msg.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        for KeyValue(k, v) in headers do
            msg.Headers.TryAddWithoutValidation(k, v) |> ignore
        msg

    new() = HttpChatTransport(sharedClient)

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
        AsyncEnumerableBridge.ofProducerWithCancellation cancellationToken produce

    interface IChatTransport with
        member this.Post(url, headers, body, cancellationToken) = this.Post(url, headers, body, cancellationToken)
        member this.PostStream(url, headers, body, cancellationToken) = this.PostStream(url, headers, body, cancellationToken)

/// Standalone chat model: IChatProtocol wire format over an IChatTransport endpoint.
[<Sealed>]
type ChatClient private (model: string, protocol: IChatProtocol, transport: IChatTransport, url: string, headers: IReadOnlyDictionary<string, string>, backend: IChatBackend) =

    let resolveBackend (request: ChatRequest) =
        if isNull backend then null
        else
            match backend with
            | :? IRequestScopedBackend as scoped ->
                let requestModel = if isNull request || String.IsNullOrEmpty request.Model then model else request.Model
                scoped.ResolveBackend(requestModel)
            | _ -> backend

    new(model: string, protocol: IChatProtocol, transport: IChatTransport, url: string, headers: IReadOnlyDictionary<string, string>) =
        ChatClient(model, protocol, transport, url, headers, null)

    new(model: string, backend: IChatBackend, transport: IChatTransport) =
        ChatClient(model, null, transport, null, null, backend)

    /// Model id sent with every request.
    member _.Model = model

    /// Runs a non-streaming completion; a Stream flag on the request is treated as false.
    member _.Complete(request: ChatRequest, cancellationToken: CancellationToken) : Task<ChatResponse> =
        task {
            let actualBackend = resolveBackend request
            let mustStream =
                match actualBackend with
                | :? IStreamingCompletionBackend as streaming -> streaming.UseStreamingForCompletions
                | _ -> false
            if mustStream then
                let! prepared = actualBackend.PrepareRequest(request, true, cancellationToken)
                let lines = transport.PostStream(prepared.Url, prepared.Headers, prepared.Body, cancellationToken)
                use enumerator = lines.GetAsyncEnumerator(cancellationToken)
                let parser = actualBackend.CreateStreamParser()
                let mutable more = true
                while more do
                    cancellationToken.ThrowIfCancellationRequested()
                    let! hasNext = enumerator.MoveNextAsync().AsTask()
                    more <- hasNext
                    if hasNext then parser.PushLine(enumerator.Current) |> ignore
                parser.Finish() |> ignore
                match parser with
                | :? IChatCompletionStreamParser as completionParser -> return completionParser.GetResponse()
                | _ -> return raise (AiException(AiError.Decode "Streaming completion backend has no completed response parser."))
            else
                let! prepared =
                    if not (isNull actualBackend) then
                        actualBackend.PrepareRequest(request, false, cancellationToken)
                    else
                        Task.FromResult(PreparedChatRequest(url, headers, protocol.SerializeRequest(request, false)))
                let! response = transport.Post(prepared.Url, prepared.Headers, prepared.Body, cancellationToken)
                if response.StatusCode >= 200 && response.StatusCode <= 299 then
                    return if not (isNull actualBackend) then actualBackend.ParseResponse(response.Body) else protocol.ParseResponse(response.Body)
                else
                    return raise (AiException(AiError.Http(response.StatusCode, response.Body)))
        }

    /// Streams parsed ChatStreamEvents, skipping lines the protocol maps to null.
    member _.Stream(request: ChatRequest, cancellationToken: CancellationToken) : IAsyncEnumerable<ChatStreamEvent> =
        let actualBackend = resolveBackend request
        let produce (writer: ChannelWriter<ChatStreamEvent>) (ct: CancellationToken) : Task<unit> =
            task {
                let! prepared =
                    if not (isNull actualBackend) then
                        actualBackend.PrepareRequest(request, true, ct)
                    else
                        Task.FromResult(PreparedChatRequest(url, headers, protocol.SerializeRequest(request, true)))
                let lines = transport.PostStream(prepared.Url, prepared.Headers, prepared.Body, ct)
                use enumerator = lines.GetAsyncEnumerator(ct)
                if not (isNull actualBackend) then
                    // Parser state belongs to this enumeration; concurrent streams never share it.
                    let parser = actualBackend.CreateStreamParser()
                    let mutable more = true
                    while more do
                        let! hasNext = enumerator.MoveNextAsync().AsTask()
                        more <- hasNext
                        if hasNext then
                            for evt in parser.PushLine(enumerator.Current) do
                                if not (Object.ReferenceEquals(evt, null)) then
                                    do! writer.WriteAsync(evt, ct).AsTask()
                    for evt in parser.Finish() do
                        if not (Object.ReferenceEquals(evt, null)) then
                            do! writer.WriteAsync(evt, ct).AsTask()
                else
                    let mutable more = true
                    while more do
                        let! hasNext = enumerator.MoveNextAsync().AsTask()
                        more <- hasNext
                        if hasNext then
                            let evt = protocol.TryParseStreamLine(enumerator.Current)
                            if not (Object.ReferenceEquals(evt, null)) then
                                do! writer.WriteAsync(evt, ct).AsTask()
            }
        AsyncEnumerableBridge.ofProducerWithCancellation cancellationToken produce

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

        let transport =
            if isNull transport then
                HttpChatTransport() :> IChatTransport
            else
                transport

        let options = ProviderOptions()
        options.BaseUrl <- baseUrl
        let credentials = ApiKeyCredentialProvider(apiKey) :> ICredentialProvider
        let backend = OpenAIChatBackend(model, null, credentials, options) :> IChatBackend
        ChatClient(model, backend, transport)
