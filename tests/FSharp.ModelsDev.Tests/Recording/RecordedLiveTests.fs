namespace FSharp.ModelsDev.Tests

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.ModelsDev
open FSharp.ModelsDev.Tests.Recording

/// Recording has a separate opt-in from ordinary live tests and requires an explicit output directory.
type RecordingFactAttribute(prefix: string) as this =
    inherit FactAttribute()
    do
        let value name = Environment.GetEnvironmentVariable name
        let required = [| "MODELSDEV_RECORDINGS_DIR"; "MODELSDEV_" + prefix + "_KEY"; "MODELSDEV_" + prefix + "_MODEL" |]
        if value "MODELSDEV_RECORD" <> "1" || value "MODELSDEV_LIVE_TESTS" <> "1" then
            this.Skip <- "Set MODELSDEV_RECORD=1 and MODELSDEV_LIVE_TESTS=1 to record production API traffic."
        elif required |> Array.exists (fun name -> String.IsNullOrWhiteSpace(value name)) then
            this.Skip <- "Configure the recordings directory and provider key/model explicitly."

type private RecordingCredentials(protocol: string, key: string) =
    interface ICredentialProvider with
        member _.GetHeaders(_, ct) =
            ct.ThrowIfCancellationRequested()
            let headers = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            match protocol with
            | "gemini" -> headers["x-goog-api-key"] <- key
            | "anthropic-messages" -> headers["x-api-key"] <- key
            | _ -> headers["Authorization"] <- "Bearer " + key
            Task.FromResult(headers :> IReadOnlyDictionary<string, string>)

module private LiveRecording =
    let run (prefix: string) (protocol: string) (url: string) = task {
        let value suffix = Environment.GetEnvironmentVariable("MODELSDEV_" + prefix + "_" + suffix)
        let scenario = JsonObject()
        scenario["protocol"] <- JsonValue.Create protocol
        scenario["model"] <- JsonValue.Create(value "MODEL")
        scenario["system"] <- JsonValue.Create "Follow the user's instructions."
        scenario["prompt"] <- JsonValue.Create "Reply with the single word OK."
        scenario["maxTokens"] <- JsonValue.Create 128
        scenario["tools"] <- JsonValue.Create false
        let path = RecordingFiles.fixture("local/" + protocol)
        use source = new HttpClientHandler()
        use handler = new RecordedHttpHandler(path, Record, source)
        handler.Metadata["scenario"] <- scenario
        use http = new HttpClient(handler)
        let credentials = RecordingCredentials(protocol, value "KEY")
        let backend = RecordedScenario.backendWithCredentials scenario url credentials
        let client = ChatClient(value "MODEL", backend, HttpChatTransport(http)) :> IChatModel
        let messages = [| ChatMessage.System(RecordedScenario.text scenario "system"); ChatMessage.User(RecordedScenario.text scenario "prompt") |]
        let request = RecordedScenario.makeRequest scenario messages
        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 90.)
        let! complete = client.Complete(request, timeout.Token)
        Assert.False(String.IsNullOrWhiteSpace complete.Message.Text)
        // The stream helper uses the caller token below through an explicit forwarding model.
        let bounded =
            { new IChatModel with
                member _.Model = client.Model
                member _.Complete(request, _) = client.Complete(request, timeout.Token)
                member _.Stream(request, _) = client.Stream(request, timeout.Token) }
        let! streamed, _ = RecordedScenario.stream bounded request
        Assert.False(String.IsNullOrWhiteSpace streamed.Text)
        handler.Save()
    }

type RecordedLiveTests() =
    [<RecordingFact("OPENAI"); Trait("Category", "Recording")>]
    member _.``record OpenAI Chat text``() = LiveRecording.run "OPENAI" "openai-chat" "https://api.openai.com/v1/chat/completions"

    [<RecordingFact("OPENAI"); Trait("Category", "Recording")>]
    member _.``record OpenAI Responses text``() = LiveRecording.run "OPENAI" "openai-responses" "https://api.openai.com/v1/responses"

    [<RecordingFact("ANTHROPIC"); Trait("Category", "Recording")>]
    member _.``record Anthropic Messages text``() = LiveRecording.run "ANTHROPIC" "anthropic-messages" "https://api.anthropic.com/v1/messages"

    [<RecordingFact("GEMINI"); Trait("Category", "Recording")>]
    member _.``record Gemini text``() = LiveRecording.run "GEMINI" "gemini" "https://generativelanguage.googleapis.com/v1beta/models/unused:generateContent"

    [<Fact; Trait("Category", "Recorded")>]
    member _.``replay every locally recorded text scenario offline``() = task {
        let directory = Path.GetDirectoryName(RecordingFiles.fixture "local/probe")
        let files = if Directory.Exists directory then Directory.GetFiles(directory, "*.json") else Array.empty
        for path in files do
            let cassette = CassetteJson.load path
            let scenario = cassette.Metadata["scenario"]
            use handler = new RecordedHttpHandler(path)
            use http = new HttpClient(handler)
            let backend = RecordedScenario.backend scenario cassette.Interactions[0].Request.Url
            let client = ChatClient(RecordedScenario.text scenario "model", backend, HttpChatTransport(http)) :> IChatModel
            let messages = [| ChatMessage.System(RecordedScenario.text scenario "system"); ChatMessage.User(RecordedScenario.text scenario "prompt") |]
            let request = RecordedScenario.makeRequest scenario messages
            let! complete = client.Complete(request, CancellationToken.None)
            Assert.False(String.IsNullOrWhiteSpace complete.Message.Text)
            let! streamed, _ = RecordedScenario.stream client request
            Assert.False(String.IsNullOrWhiteSpace streamed.Text)
            handler.VerifyComplete()
    }
