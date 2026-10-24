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

    if checking then
        if ArtifactPublication.check output artifacts then
            0
        else
            eprintfn "Contract artifacts differ. Regenerate with --output contracts."
            1
    else
        ArtifactPublication.publish output artifacts
        0
