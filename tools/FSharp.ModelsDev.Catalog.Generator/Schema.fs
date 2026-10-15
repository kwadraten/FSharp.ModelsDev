/// Raw record types for the models.dev api.json snapshot plus a tolerant JsonDocument parser.
module FSharp.ModelsDev.Catalog.Generator.Schema

open System.Text.Json

type ModelRaw =
    { Id: string
      Name: string
      ToolCall: bool
      Reasoning: bool
      StructuredOutput: bool
      ContextLimit: int64 option
      OutputLimit: int64 option
      InputModalities: string list }

type ProviderRaw =
    { Id: string
      Name: string
      Npm: string
      Api: string
      Doc: string
      Env: string list
      Models: ModelRaw list }

let private stringProperty (el: JsonElement) (name: string) (fallback: string) : string =
    match el.TryGetProperty(name) with
    | true, p when p.ValueKind = JsonValueKind.String -> p.GetString()
    | _ -> fallback

let private boolProperty (el: JsonElement) (name: string) : bool =
    match el.TryGetProperty(name) with
    | true, p when p.ValueKind = JsonValueKind.True -> true
    | _ -> false

let private int64OptionProperty (el: JsonElement) (name: string) : int64 option =
    match el.TryGetProperty(name) with
    | true, p when p.ValueKind = JsonValueKind.Number ->
        match p.TryGetInt64() with
        | true, value -> Some value
        | _ -> None
    | _ -> None

let private stringListProperty (el: JsonElement) (name: string) : string list =
    match el.TryGetProperty(name) with
    | true, p when p.ValueKind = JsonValueKind.Array ->
        [ for item in p.EnumerateArray() do
              if item.ValueKind = JsonValueKind.String then
                  yield item.GetString() ]
    | _ -> []

let private limitProperty (el: JsonElement) (name: string) : int64 option =
    match el.TryGetProperty("limit") with
    | true, limit when limit.ValueKind = JsonValueKind.Object -> int64OptionProperty limit name
    | _ -> None

let private inputModalities (el: JsonElement) : string list =
    match el.TryGetProperty("modalities") with
    | true, modalities when modalities.ValueKind = JsonValueKind.Object -> stringListProperty modalities "input"
    | _ -> []

let private parseModel (fallbackId: string) (el: JsonElement) : ModelRaw =
    { Id = stringProperty el "id" fallbackId
      Name = stringProperty el "name" ""
      ToolCall = boolProperty el "tool_call"
      Reasoning = boolProperty el "reasoning"
      StructuredOutput = boolProperty el "structured_output"
      ContextLimit = limitProperty el "context"
      OutputLimit = limitProperty el "output"
      InputModalities = inputModalities el }

let private parseProvider (el: JsonElement) : ProviderRaw =
    let models =
        match el.TryGetProperty("models") with
        | true, models when models.ValueKind = JsonValueKind.Object ->
            [ for p in models.EnumerateObject() -> parseModel p.Name p.Value ]
        | _ -> []

    { Id = stringProperty el "id" ""
      Name = stringProperty el "name" ""
      Npm = stringProperty el "npm" ""
      Api = stringProperty el "api" ""
      Doc = stringProperty el "doc" ""
      Env = stringListProperty el "env"
      Models = models }

/// Parses the whole api.json payload; missing fields fall back to defaults.
let parseApiJson (json: string) : ProviderRaw list =
    use doc = JsonDocument.Parse(json)
    [ for p in doc.RootElement.EnumerateObject() -> parseProvider p.Value ]
