module ClaimCore.ContractGenerator.Program

open System
open System.IO
open ClaimCore.Domain
open ClaimCore.Presentation
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

    for issue in AuthoredMessages.issues () do
        eprintfn "Translation %s: %s %s (English fallback)." issue.Reason issue.Language issue.Key

    if checking then
        if
            (AuthoredMessages.requiredIssues ()).IsEmpty
            && ArtifactPublication.check output artifacts
        then
            0
        else
            eprintfn "Generated contracts differ. Regenerate with --output contracts."
            1
    else
        ArtifactPublication.publish output artifacts
        0
