namespace FSharp.ModelsDev.Tests.Recording

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks

// HTTP-only port of OpenCode's cassette approach. No Effect/Bun or CLI dependency.
// See Fixtures/Recordings/NOTICE.md for the pinned upstream source and license.
type internal CassetteMode = Replay | Record

[<AllowNullLiteral>]
type RecordedRequest() =
    member val Method = "" with get, set
    member val Url = "" with get, set
    member val Headers = Dictionary<string, string>() with get, set
    member val Body = "" with get, set

[<AllowNullLiteral>]
type RecordedResponse() =
    member val Status = 200 with get, set
    member val Headers = Dictionary<string, string>() with get, set
    member val Body = "" with get, set

[<AllowNullLiteral>]
type RecordedInteraction() =
    member val Transport = "http" with get, set
    member val Request: RecordedRequest = null with get, set
    member val Response: RecordedResponse = null with get, set

[<AllowNullLiteral>]
type Cassette() =
    member val Version = 1 with get, set
    member val Metadata = JsonObject() with get, set
    member val Interactions = ResizeArray<RecordedInteraction>() with get, set

module internal CassetteJson =
    let options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true)
    let load (path: string) =
        if not (File.Exists path) then
            raise (FileNotFoundException("Required recording is missing; replay never falls back to the network.", path))
        let text = File.ReadAllText path
        use document = JsonDocument.Parse text
        let root = document.RootElement
        if root.ValueKind <> JsonValueKind.Object then invalidOp "Invalid HTTP cassette."
        match root.TryGetProperty "version" with
        | true, version when version.ValueKind = JsonValueKind.Number && version.GetRawText() = "1" -> ()
        | _ -> invalidOp "Invalid or unsupported HTTP cassette version."
        let cassette = JsonSerializer.Deserialize<Cassette>(text, options)
        if isNull cassette || cassette.Version <> 1 || isNull cassette.Interactions || cassette.Interactions.Count = 0 then
            invalidOp "Invalid or unsupported HTTP cassette."
        for item in cassette.Interactions do
            if isNull item || item.Transport <> "http" || isNull item.Request || isNull item.Response
               || String.IsNullOrWhiteSpace item.Request.Method || String.IsNullOrWhiteSpace item.Request.Url
               || isNull item.Request.Headers || isNull item.Response.Headers || isNull item.Response.Body
               || item.Response.Status < 100 || item.Response.Status > 599 then
                invalidOp "Invalid HTTP interaction in cassette."
        cassette

    let bodyEquals (left: string) (right: string) =
        try JsonNode.DeepEquals(JsonNode.Parse left, JsonNode.Parse right)
        with :? JsonException -> String.Equals(left, right, StringComparison.Ordinal)

/// Headers are allowlisted. Known credentials are also removed if echoed in text or SSE.
type internal CassetteRedactor() =
    let secrets = HashSet<string>(StringComparer.Ordinal)
    let names = HashSet<string>([| "authorization"; "proxy-authorization"; "cookie"; "set-cookie";
                                   "x-api-key"; "x-goog-api-key"; "api_key"; "apikey"; "api-key";
                                   "access_token"; "refresh_token"; "id_token"; "client_secret";
                                   "password"; "device_code"; "token" |], StringComparer.OrdinalIgnoreCase)
    let remember (value: string) =
        if not (String.IsNullOrEmpty value) && value <> "[REDACTED]" then secrets.Add value |> ignore
    let replaceSecrets (text: string) =
        if isNull text then ""
        else
            // Short credentials are scrubbed as structured values, never as substrings of JSON syntax.
            (text, secrets |> Seq.filter (fun value -> value.Length >= 8) |> Seq.sortByDescending String.length)
            ||> Seq.fold (fun current secret -> current.Replace(secret, "[REDACTED]", StringComparison.Ordinal))
    let isSensitive request (name: string) = names.Contains name || (request && (name = "code" || name = "key"))
    let scrubJson request (text: string) =
        try
            let node = JsonNode.Parse text
            let mutable changed = false
            let rec scan (node: JsonNode) =
                match node with
                | :? JsonObject as obj ->
                    for pair in obj do
                        match pair.Value with
                        | :? JsonValue as value when isSensitive request pair.Key ->
                            match value.TryGetValue<string>() with true, secret -> remember secret | _ -> ()
                        | value -> scan value
                | :? JsonArray as items -> for value in items do scan value
                | _ -> ()
            scan node
            let rec walk (node: JsonNode) =
                match node with
                | :? JsonObject as obj ->
                    for pair in Seq.toArray obj do
                        match pair.Value with
                        | :? JsonValue as value ->
                            match value.TryGetValue<string>() with
                            | true, secret ->
                                let cleaned =
                                    if isSensitive request pair.Key || secrets.Contains secret then "[REDACTED]"
                                    else replaceSecrets secret
                                if cleaned <> secret then
                                    obj[pair.Key] <- JsonValue.Create(cleaned)
                                    changed <- true
                            | _ -> walk pair.Value
                        | value -> walk value
                | :? JsonArray as items ->
                    for index in 0 .. items.Count - 1 do
                        match items[index] with
                        | :? JsonValue as value ->
                            match value.TryGetValue<string>() with
                            | true, text ->
                                let cleaned = if secrets.Contains text then "[REDACTED]" else replaceSecrets text
                                if cleaned <> text then items[index] <- JsonValue.Create(cleaned); changed <- true
                            | _ -> ()
                        | value -> walk value
                | _ -> ()
            walk node
            if changed then node.ToJsonString() else text
        with :? JsonException -> text
    let scrubForm (text: string) =
        text.Split('&')
        |> Array.map (fun pair ->
            let parts = pair.Split('=', 2)
            if parts.Length = 2 && isSensitive true (Uri.UnescapeDataString parts[0]) then
                remember (Uri.UnescapeDataString(parts[1].Replace('+', ' ')))
                parts[0] + "=%5BREDACTED%5D"
            else pair)
        |> String.concat "&"

    member _.Headers(headers: seq<KeyValuePair<string, string>>) =
        let result = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        for KeyValue(name, value) in headers do
            if names.Contains name then
                remember value
                if name.Equals("authorization", StringComparison.OrdinalIgnoreCase) then
                    let parts = value.Split(' ', 2)
                    if parts.Length = 2 then remember parts[1]
                elif name.Equals("cookie", StringComparison.OrdinalIgnoreCase) || name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase) then
                    let pairs = value.Split(';')
                    let pairs = if name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase) then Array.truncate 1 pairs else pairs
                    for pair in pairs do
                        let parts = pair.Trim().Split('=', 2)
                        if parts.Length = 2 then remember parts[1]
            elif name.Equals("content-type", StringComparison.OrdinalIgnoreCase) then
                result["content-type"] <- value.Split(';').[0].Trim().ToLowerInvariant()
            elif name.Equals("anthropic-version", StringComparison.OrdinalIgnoreCase) then
                result["anthropic-version"] <- value
        result

    member _.Url(url: string) =
        let uri = Uri(url, UriKind.Absolute)
        if uri.UserInfo <> "" then invalidOp "Credentials in URL userinfo are not supported by recordings."
        let query = uri.Query.TrimStart('?')
        let query = if query = "" then "" else "?" + scrubForm query
        uri.GetLeftPart(UriPartial.Path) + query

    member _.Body(request: bool, body: string) =
        let body = if isNull body then "" else body
        let result =
            if body.TrimStart().StartsWith("{") || body.TrimStart().StartsWith("[") then scrubJson request body
            elif body.Contains("data:") then
                body.Split('\n')
                |> Array.map (fun line ->
                    if line.StartsWith("data:", StringComparison.Ordinal) then
                        let start = line.IndexOf(':') + 1
                        let data = line.Substring(start)
                        let cleaned = scrubJson request data
                        if cleaned = data then line else "data: " + cleaned
                    else line)
                |> String.concat "\n"
            elif request && body.Contains('=') then scrubForm body
            else body
        replaceSecrets result

/// Deliberately fragments replay bytes, including UTF-8/SSE boundaries, without simulating timing.
type private FragmentedStream(bytes: byte[], finished: unit -> unit) =
    inherit Stream()
    let mutable position = 0
    let mutable reads = 0
    let mutable complete = false
    let read (destination: Span<byte>) =
        if position = bytes.Length then
            if not complete then complete <- true; finished()
            0
        else
            let sizes = [| 1; 7; 23; 5; 4096 |]
            let count = min destination.Length (min sizes[reads % sizes.Length] (bytes.Length - position))
            bytes.AsSpan(position, count).CopyTo destination
            position <- position + count
            reads <- reads + 1
            count
    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = int64 bytes.Length
    override _.Position with get() = int64 position and set _ = raise (NotSupportedException())
    override _.Read(buffer, offset, count) = read (buffer.AsSpan(offset, count))
    override _.ReadAsync(buffer: Memory<byte>, ct: CancellationToken) =
        ct.ThrowIfCancellationRequested()
        ValueTask<int>(read buffer.Span)
    override _.ReadAsync(buffer: byte[], offset, count, ct) =
        ct.ThrowIfCancellationRequested()
        Task.FromResult(read (buffer.AsSpan(offset, count)))
    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength(_) = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())

type private ReplayContent(body: string, finished: unit -> unit) =
    inherit HttpContent()
    let bytes = Encoding.UTF8.GetBytes body
    override _.TryComputeLength(length: byref<int64>) = length <- int64 bytes.Length; true
    override _.CreateContentReadStream(ct: CancellationToken) =
        ct.ThrowIfCancellationRequested()
        new FragmentedStream(bytes, finished) :> Stream
    override _.CreateContentReadStreamAsync() = Task.FromResult(new FragmentedStream(bytes, finished) :> Stream)
    override _.CreateContentReadStreamAsync(ct: CancellationToken) =
        ct.ThrowIfCancellationRequested()
        Task.FromResult(new FragmentedStream(bytes, finished) :> Stream)
    override _.SerializeToStreamAsync(destination, _) = task {
        use source = new FragmentedStream(bytes, finished)
        do! source.CopyToAsync(destination)
    }

/// Cassette v1 HTTP handler: strict ordered matching, explicit record, no missing-file/network fallback.
type internal RecordedHttpHandler(path: string, mode: CassetteMode, liveHandler: HttpMessageHandler, environment: string -> string) =
    inherit HttpMessageHandler()
    let gate = obj()
    let path = Path.GetFullPath path
    let redactor = CassetteRedactor()
    let cassette =
        match mode with
        | Replay -> CassetteJson.load path
        | Record ->
            if not (String.IsNullOrEmpty(environment "CI")) || not (String.IsNullOrEmpty(environment "GITHUB_ACTIONS")) then
                invalidOp "Recording is prohibited in CI."
            if File.Exists path then invalidOp "Recording refuses to overwrite an existing cassette; remove the selected file explicitly first."
            if isNull liveHandler then nullArg "liveHandler"
            Cassette()
    let invoker = if mode = Record then new HttpMessageInvoker(liveHandler, true) else null
    let mutable next = 0
    let mutable active = 0
    let mutable saved = false
    let mutable failed = false
    let headersOf (message: HttpRequestMessage) =
        seq {
            for h in message.Headers do yield KeyValuePair(h.Key, String.Join(", ", h.Value))
            if not (isNull message.Content) then
                for h in message.Content.Headers do yield KeyValuePair(h.Key, String.Join(", ", h.Value))
        }
    let cleanRequest (request: RecordedRequest) =
        let result = RecordedRequest(Method = request.Method, Url = redactor.Url request.Url,
                                     Headers = redactor.Headers request.Headers)
        result.Body <- redactor.Body(true, request.Body)
        result
    let cleanResponse (response: RecordedResponse) =
        let result = RecordedResponse(Status = response.Status, Headers = redactor.Headers response.Headers)
        result.Body <- redactor.Body(false, response.Body)
        result

    new(path: string) = new RecordedHttpHandler(path, Replay, null, Environment.GetEnvironmentVariable)
    new(path: string, mode: CassetteMode, liveHandler: HttpMessageHandler) =
        new RecordedHttpHandler(path, mode, liveHandler, Environment.GetEnvironmentVariable)

    member _.Metadata = cassette.Metadata
    member _.InteractionCount = cassette.Interactions.Count
    member _.VerifyComplete() = lock gate (fun () ->
        if failed || active <> 0 || next <> cassette.Interactions.Count || next = 0 then
            invalidOp "Recording has failed, unread responses, or unconsumed interactions.")

    member this.Save() =
        if mode <> Record then invalidOp "Replay recordings are read-only."
        this.VerifyComplete()
        lock gate (fun () ->
            if saved then invalidOp "Recording has already been saved."
            for item in cassette.Interactions do
                item.Request <- cleanRequest item.Request
                item.Response <- cleanResponse item.Response
            cassette.Metadata <- JsonNode.Parse(redactor.Body(false, cassette.Metadata.ToJsonString())).AsObject()
            cassette.Metadata["recordedAt"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("O"))
            let directory = Path.GetDirectoryName path
            Directory.CreateDirectory directory |> ignore
            let temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp")
            try
                File.WriteAllText(temporary, JsonSerializer.Serialize(cassette, CassetteJson.options), UTF8Encoding(false))
                File.Move(temporary, path, false)
                saved <- true
            finally
                if File.Exists temporary then File.Delete temporary)

    override _.SendAsync(message: HttpRequestMessage, ct: CancellationToken) = task {
        ct.ThrowIfCancellationRequested()
        let! body = if isNull message.Content then Task.FromResult "" else message.Content.ReadAsStringAsync ct
        let actual = RecordedRequest(Method = message.Method.Method, Url = message.RequestUri.AbsoluteUri,
                                     Headers = Dictionary<string, string>(), Body = body)
        for KeyValue(name, value) in headersOf message do actual.Headers[name] <- value
        let item = lock gate (fun () ->
            if saved then invalidOp "A saved recording cannot accept more requests."
            let actual = cleanRequest actual
            let selected =
                if mode = Record then
                    let entry = RecordedInteraction(Request = actual)
                    cassette.Interactions.Add entry
                    entry
                else
                    if next >= cassette.Interactions.Count then
                        failed <- true
                        invalidOp "Unexpected request: all recorded interactions are consumed."
                    let expected = cassette.Interactions[next]
                    let wanted = cleanRequest expected.Request
                    let mismatch =
                        if wanted.Method <> actual.Method then "method"
                        elif wanted.Url <> actual.Url then "URL"
                        elif wanted.Headers.Count <> actual.Headers.Count
                             || wanted.Headers |> Seq.exists (fun pair ->
                                 match actual.Headers.TryGetValue pair.Key with true, value -> value <> pair.Value | _ -> true) then "headers"
                        elif not (CassetteJson.bodyEquals wanted.Body actual.Body) then "body"
                        else ""
                    if mismatch <> "" then
                        failed <- true
                        // Never echo request data, even in diagnostic failures.
                        invalidOp $"Recorded request mismatch at interaction {next + 1}: {mismatch}."
                    expected
            next <- next + 1
            active <- active + 1
            selected)
        try
            let mutable wireResponse = item.Response
            if mode = Record then
                use! response = invoker.SendAsync(message, ct)
                // Record full UTF-8 bodies, including raw SSE. This intentionally does not record timing.
                let! text = response.Content.ReadAsStringAsync ct
                let captured = RecordedResponse(Status = int response.StatusCode, Body = text)
                for header in response.Headers do captured.Headers[header.Key] <- String.Join(", ", header.Value)
                for header in response.Content.Headers do captured.Headers[header.Key] <- String.Join(", ", header.Value)
                lock gate (fun () -> item.Response <- cleanResponse captured)
                wireResponse <- captured
            let response = new HttpResponseMessage(enum<HttpStatusCode> wireResponse.Status)
            response.RequestMessage <- message
            response.Content <- new ReplayContent(wireResponse.Body, fun () -> lock gate (fun () -> active <- active - 1))
            for KeyValue(name, value) in wireResponse.Headers do
                if not (response.Content.Headers.TryAddWithoutValidation(name, value)) then
                    response.Headers.TryAddWithoutValidation(name, value) |> ignore
            return response
        with error ->
            lock gate (fun () -> failed <- true)
            return raise error
    }

    override _.Dispose(disposing) =
        if disposing && not (isNull invoker) then invoker.Dispose()
        base.Dispose(disposing)

module internal RecordingFiles =
    let fixture (name: string) =
        let parts = name.Replace('\\', '/').Split('/')
        if parts.Length = 0 || parts |> Array.exists (fun part ->
            String.IsNullOrEmpty part || part = "." || part = ".."
            || part |> Seq.exists (fun c -> not (Char.IsAsciiLetterOrDigit c || c = '-' || c = '_'))) then
            invalidArg "name" "Use slash-separated alphanumeric recording names."
        let configured = Environment.GetEnvironmentVariable "MODELSDEV_RECORDINGS_DIR"
        let root = if String.IsNullOrWhiteSpace configured then Path.Combine(AppContext.BaseDirectory, "Fixtures", "Recordings") else configured
        Path.Combine(Path.GetFullPath root, String.Join(Path.DirectorySeparatorChar, parts) + ".json")
