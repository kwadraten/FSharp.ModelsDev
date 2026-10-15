/// Deterministic ordering and protocol inference over the raw snapshot.
module FSharp.ModelsDev.Catalog.Generator.Normalize

type NormalizedModel =
    { Id: string
      Name: string
      ToolCall: bool
      Reasoning: bool
      StructuredOutput: bool
      ContextLimit: int64 option
      OutputLimit: int64 option
      InputModalities: string list }

type NormalizedProvider =
    { Id: string
      Name: string
      Npm: string
      Api: string
      Doc: string
      Env: string list
      ProtocolId: string
      Models: NormalizedModel list }

/// Maps the provider npm package onto the wire protocol the SDK implements for it.
let inferProtocolId (npm: string) : string =
    if System.String.IsNullOrEmpty(npm) then "unknown"
    elif npm.Contains("openai") then "openai-chat"
    elif npm.Contains("anthropic") then "anthropic-messages"
    elif npm.Contains("google") then "gemini"
    else "unknown"

let private normalizeModel (model: Schema.ModelRaw) : NormalizedModel =
    { Id = model.Id
      Name = model.Name
      ToolCall = model.ToolCall
      Reasoning = model.Reasoning
      StructuredOutput = model.StructuredOutput
      ContextLimit = model.ContextLimit
      OutputLimit = model.OutputLimit
      InputModalities = model.InputModalities }

/// Sorts providers by id and each provider's models by id.
let normalize (providers: Schema.ProviderRaw list) : NormalizedProvider list =
    providers
    |> List.map (fun provider ->
        { Id = provider.Id
          Name = provider.Name
          Npm = provider.Npm
          Api = provider.Api
          Doc = provider.Doc
          Env = provider.Env
          ProtocolId = inferProtocolId provider.Npm
          Models = provider.Models |> List.map normalizeModel |> List.sortBy (fun model -> model.Id) })
    |> List.sortBy (fun provider -> provider.Id)
