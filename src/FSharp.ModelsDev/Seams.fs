namespace FSharp.ModelsDev

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

[<Sealed; AllowNullLiteral>]
type PreparedChatRequest(url: string, headers: IReadOnlyDictionary<string, string>, body: string) =
    member _.Url = url
    member _.Headers = headers
    member _.Body = body

[<AllowNullLiteral>]
type IChatStreamParser =
    abstract PushLine: line: string -> IReadOnlyList<ChatStreamEvent>
    abstract Finish: unit -> IReadOnlyList<ChatStreamEvent>

[<AllowNullLiteral>]
type IChatBackend =
    abstract PrepareRequest: request: ChatRequest * stream: bool * cancellationToken: CancellationToken -> Task<PreparedChatRequest>
    abstract ParseResponse: body: string -> ChatResponse
    abstract CreateStreamParser: unit -> IChatStreamParser

/// Backends whose upstream requires streaming even for a completed-response call.
[<AllowNullLiteral>]
type IStreamingCompletionBackend =
    abstract UseStreamingForCompletions: bool

/// A stream parser capable of returning the authoritative completed response.
[<AllowNullLiteral>]
type IChatCompletionStreamParser =
    abstract GetResponse: unit -> ChatResponse

[<AllowNullLiteral>]
type internal IRequestScopedBackend =
    abstract ResolveBackend: model: string -> IChatBackend

[<Sealed; AllowNullLiteral>]
type TransportResponse(statusCode: int, body: string) =
    member _.StatusCode = statusCode
    member _.Body = body

/// HTTP seam. Implementations never see model catalog data; tests stub this.
[<AllowNullLiteral>]
type IChatTransport =
    abstract Post:
        url: string *
        headers: IReadOnlyDictionary<string, string> *
        body: string *
        cancellationToken: CancellationToken ->
            Task<TransportResponse>

    /// Returns the raw lines of an SSE stream (data:, event:, comments, blanks).
    abstract PostStream:
        url: string *
        headers: IReadOnlyDictionary<string, string> *
        body: string *
        cancellationToken: CancellationToken ->
            IAsyncEnumerable<string>

/// Protocol seam: translates contract types to/from wire format.
/// One implementation per ProtocolId (e.g. "openai-chat").
[<AllowNullLiteral>]
type IChatProtocol =
    /// Serializes a request. `stream` selects the streaming wire shape.
    abstract SerializeRequest: request: ChatRequest * stream: bool -> string

    abstract ParseResponse: body: string -> ChatResponse

    /// Returns the event for one raw SSE line, or null to skip the line
    /// (blanks, comments, event: lines, terminal markers).
    abstract TryParseStreamLine: line: string -> ChatStreamEvent

/// Top-level model seam. Standalone entry point; also adapts to IChatClient.
[<AllowNullLiteral>]
type IChatModel =
    abstract Model: string

    abstract Complete:
        request: ChatRequest * cancellationToken: CancellationToken -> Task<ChatResponse>

    abstract Stream:
        request: ChatRequest * cancellationToken: CancellationToken -> IAsyncEnumerable<ChatStreamEvent>

/// Authentication seam. Knows nothing about model capabilities.
[<AllowNullLiteral>]
type ICredentialProvider =
    abstract GetHeaders:
        accountId: string * cancellationToken: CancellationToken -> Task<IReadOnlyDictionary<string, string>>
