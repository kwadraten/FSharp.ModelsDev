namespace FSharp.ModelsDev.MEAi

open System
open System.Runtime.CompilerServices
open System.Threading
open FSharp.ModelsDev
open Microsoft.Extensions.AI

/// Adapts an FSharp.ModelsDev.IChatModel to a Microsoft.Extensions.AI.IChatClient.
[<Sealed>]
type ModelsDevChatClient(model: FSharp.ModelsDev.IChatModel) =
    do
        if Object.ReferenceEquals(model, null) then
            nullArg (nameof model)
    let metadata = ChatClientMetadata("fsharp-aisdk", null, model.Model)

    interface IChatClient with
        member _.GetResponseAsync(messages, options, cancellationToken) =
            if isNull messages then
                nullArg (nameof messages)
            let request = Conversions.requestFromAi model.Model messages options false
            task {
                let! response = model.Complete(request, cancellationToken)
                return Conversions.responseToAi response
            }

        member _.GetStreamingResponseAsync(messages, options, cancellationToken) =
            if isNull messages then
                nullArg (nameof messages)
            let request = Conversions.requestFromAi model.Model messages options true
            model.Stream(request, cancellationToken)
            |> AsyncEnumerableBridge.map Conversions.updateFromEvent cancellationToken

        member this.GetService(serviceType, serviceKey) =
            if serviceType = typeof<IChatClient> || serviceType = typeof<ModelsDevChatClient> then
                box this
            elif serviceType = typeof<ChatClientMetadata> then
                box metadata
            else
                null

        member _.Dispose() = ()

/// Extension methods for using an IChatModel as a Microsoft.Extensions.AI.IChatClient.
[<Extension>]
type ChatModelExtensions() =

    /// Wraps an IChatModel as a Microsoft.Extensions.AI.IChatClient.
    [<Extension>]
    static member AsIChatClient(model: FSharp.ModelsDev.IChatModel) : IChatClient =
        if Object.ReferenceEquals(model, null) then
            nullArg (nameof model)
        (new ModelsDevChatClient(model)) :> IChatClient
