/// CLI entry point: refreshes the snapshot and generated indexes, or verifies they are in sync.
module FSharp.ModelsDev.Catalog.Generator.Program

open System
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks

let private usageText =
    "usage:\n"
    + "  catalog-generator update-catalog [--source <url|file>] [--repo-root <path>]\n"
    + "  catalog-generator verify [--repo-root <path>]"

let private defaultSource = "https://models.dev/api.json"

type private Options =
    { Source: string option
      RepoRoot: string option }

let private emptyOptions = { Source = None; RepoRoot = None }

let rec private parseArgs args options =
    match args with
    | [] -> Ok options
    | "--source" :: value :: rest -> parseArgs rest { options with Source = Some value }
    | "--repo-root" :: value :: rest -> parseArgs rest { options with RepoRoot = Some value }
    | "--source" :: [] -> Error "--source requires a value"
    | "--repo-root" :: [] -> Error "--repo-root requires a value"
    | arg :: _ -> Error(sprintf "unknown argument: %s" arg)

let private findRepoRoot (fromDir: string) : string option =
    let rec ascend (dir: DirectoryInfo) =
        match dir with
        | null -> None
        | _ when Directory.Exists(Path.Combine(dir.FullName, "catalog")) -> Some dir.FullName
        | _ -> ascend dir.Parent

    ascend (DirectoryInfo(fromDir))

let private resolveRepoRoot (options: Options) : Result<string, string> =
    match options.RepoRoot with
    | Some root ->
        if Directory.Exists(Path.Combine(root, "catalog")) then
            Ok(Path.GetFullPath(root))
        else
            Error(sprintf "repo root %s has no catalog/ directory" root)
    | None ->
        match findRepoRoot(Directory.GetCurrentDirectory()) with
        | Some root -> Ok root
        | None -> Error("no catalog/ directory found in the working directory or its parents; pass --repo-root")

let private loadSourceBytes (source: string) : Task<byte[]> =
    if source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
       || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase) then
        task {
            use client = new HttpClient()
            return! client.GetByteArrayAsync(source)
        }
    else
        Task.FromResult(File.ReadAllBytes(source))

let private sha256Hex (bytes: byte[]) : string =
    let hash = SHA256.HashData(bytes)
    let sb = StringBuilder(hash.Length * 2)

    for b in hash do
        sb.Append(b.ToString("x2")) |> ignore

    sb.ToString()

/// Parses and normalizes a snapshot, then renders both generated index files.
let private buildOutputs (snapshotBytes: byte[]) : string * string * Normalize.NormalizedProvider list =
    let json = Encoding.UTF8.GetString(snapshotBytes)
    let providers = Schema.parseApiJson json |> Normalize.normalize
    let providerIds = providers |> List.map (fun provider -> provider.Id)

    let modelKeys =
        providers
        |> List.collect (fun provider -> provider.Models |> List.map (fun model -> (provider.Id, model.Id)))

    GenerateSource.generateProviderIndex providerIds, GenerateSource.generateModelIndex modelKeys, providers

let private writeText (path: string) (content: string) : unit =
    File.WriteAllText(path, content, UTF8Encoding(false))

let private runUpdateCatalog (options: Options) : int =
    match resolveRepoRoot options with
    | Error message ->
        eprintfn "%s" message
        1
    | Ok repoRoot ->
        let source = options.Source |> Option.defaultValue defaultSource

        try
            let snapshotBytes = (loadSourceBytes source).GetAwaiter().GetResult()
            let providerSource, modelSource, providers = buildOutputs snapshotBytes
            let catalogDir = Path.Combine(repoRoot, "catalog")
            let generatedDir = Path.Combine(repoRoot, "src", "FSharp.ModelsDev", "Generated")
            Directory.CreateDirectory(generatedDir) |> ignore

            File.WriteAllBytes(Path.Combine(catalogDir, "models.snapshot.json"), snapshotBytes)
            writeText (Path.Combine(catalogDir, "models.snapshot.sha256")) (sha256Hex snapshotBytes + "\n")
            writeText (Path.Combine(generatedDir, "ProviderIndex.g.fs")) providerSource
            writeText (Path.Combine(generatedDir, "ModelIndex.g.fs")) modelSource

            let modelCount = providers |> List.sumBy (fun provider -> provider.Models.Length)
            printfn "catalog updated from %s" source
            printfn "snapshot: %d bytes, sha256 %s" snapshotBytes.Length (sha256Hex snapshotBytes)
            printfn "generated %d providers, %d models" providers.Length modelCount
            0
        with ex ->
            eprintfn "update-catalog failed: %s" ex.Message
            1

let private runVerify (options: Options) : int =
    match resolveRepoRoot options with
    | Error message ->
        eprintfn "%s" message
        1
    | Ok repoRoot ->
        let snapshotPath = Path.Combine(repoRoot, "catalog", "models.snapshot.json")
        let shaPath = Path.Combine(repoRoot, "catalog", "models.snapshot.sha256")
        let generatedDir = Path.Combine(repoRoot, "src", "FSharp.ModelsDev", "Generated")
        let mutable ok = true

        let snapshotBytes = File.ReadAllBytes(snapshotPath)
        let actualSha = sha256Hex snapshotBytes
        let recordedSha = File.ReadAllText(shaPath).Trim()

        if not (String.Equals(actualSha, recordedSha, StringComparison.OrdinalIgnoreCase)) then
            ok <- false
            printfn "MISMATCH sha256: %s is %s but %s records %s" snapshotPath actualSha shaPath recordedSha

        let providerSource, modelSource, _ = buildOutputs snapshotBytes

        let checkFile (name: string) (expected: string) =
            let path = Path.Combine(generatedDir, name)

            if not (File.Exists(path)) then
                ok <- false
                printfn "MISSING %s" path
            else
                let actualBytes = File.ReadAllBytes(path)
                let expectedBytes = Encoding.UTF8.GetBytes(expected)

                if actualBytes <> expectedBytes then
                    ok <- false

                    printfn
                        "MISMATCH %s: disk is %d bytes, regenerated is %d bytes"
                        path
                        actualBytes.Length
                        expectedBytes.Length

        checkFile "ProviderIndex.g.fs" providerSource
        checkFile "ModelIndex.g.fs" modelSource

        if ok then
            printfn "OK: snapshot sha256 matches and generated indexes are in sync"
            0
        else
            1

[<EntryPoint>]
let main argv =
    match List.ofArray argv with
    | "update-catalog" :: rest ->
        match parseArgs rest emptyOptions with
        | Ok options -> runUpdateCatalog options
        | Error message ->
            eprintfn "%s\n%s" message usageText
            2
    | "verify" :: rest ->
        match parseArgs rest emptyOptions with
        | Ok options -> runVerify options
        | Error message ->
            eprintfn "%s\n%s" message usageText
            2
    | _ ->
        printfn "%s" usageText
        2
