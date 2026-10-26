module ClaimCore.ContractGenerator.Program

open System
open System.IO
open ClaimCore.ContractGeneration

let private invocation arguments =
    match arguments with
    | [| "--output"; directory |] when not (String.IsNullOrWhiteSpace directory) ->
        Path.GetFullPath directory, false
    | [| "--check" |] -> Path.GetFullPath "contracts", true
    | [| "--output"; directory; "--check" |]
    | [| "--check"; "--output"; directory |] when not (String.IsNullOrWhiteSpace directory) ->
        Path.GetFullPath directory, true
    | _ ->
        invalidArg (nameof arguments) "Use --output <directory> or --check [--output <directory>]."

[<EntryPoint>]
let main arguments =
    let output, checking = invocation arguments
    let artifacts = ContractArtifacts.all ()
    let documentation = CliDocumentation.source ()
    let chrome = MessageGeneration.chromeSource (CatalogValidation.validate ())

    let chromePath =
        if output = Path.GetFullPath "contracts" then
            Some(Path.GetFullPath "web/src/generated/chrome-messages.ts")
        else
            None
    // Normal checkout generation owns this reference; custom contract directories stay self-contained.
    let documentationPath =
        if output = Path.GetFullPath "contracts" then
            Some(Path.GetFullPath "docs/cli.md")
        else
            None

    if checking then
        let referenceMatches =
            documentationPath
            |> Option.forall (fun path ->
                File.Exists path && File.ReadAllBytes path = documentation)

        let chromeMatches =
            chromePath
            |> Option.forall (fun path -> File.Exists path && File.ReadAllBytes path = chrome)

        if ArtifactPublication.check output artifacts && referenceMatches && chromeMatches then
            0
        else
            eprintfn
                "Generated contracts or CLI reference differ. Regenerate with --output contracts."

            1
    else
        ArtifactPublication.publish output artifacts

        chromePath
        |> Option.iter (fun path ->
            Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
            File.WriteAllBytes(path, chrome))

        documentationPath
        |> Option.iter (fun path ->
            Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
            File.WriteAllBytes(path, documentation))

        0
