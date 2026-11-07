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

    let chromeRuntimePath =
        chromePath
        |> Option.map (fun path ->
            Path.Combine(Path.GetDirectoryName path |> nonNull, "chrome-catalogs.ts"))

    let chromeBootPath =
        chromePath
        |> Option.map (fun path ->
            Path.Combine(Path.GetDirectoryName path |> nonNull, "chrome-boot.ts"))

    let coreRuntimePath =
        chromePath
        |> Option.map (fun path ->
            Path.Combine(Path.GetDirectoryName path |> nonNull, "core-catalogs.ts"))

    let runtimeCore = MessageGeneration.catalogSource ()
    let runtimeChrome = MessageGeneration.chromeCatalogSource ()
    let loaders = MessageGeneration.catalogLoaderSource ()

    let loadersPath =
        chromePath
        |> Option.map (fun path ->
            Path.Combine(Path.GetDirectoryName path |> nonNull, "catalog-loaders.ts"))

    let bootChrome =
        MessageGeneration.chromeBootstrapSource (CatalogValidation.validate ())

    let auxiliary =
        [
            chromeRuntimePath, runtimeChrome
            chromeBootPath, bootChrome
            coreRuntimePath, runtimeCore
            loadersPath, loaders
            chromePath
            |> Option.map (fun path ->
                Path.Combine(Path.GetDirectoryName path |> nonNull, "translation-report.json")),
            System.Text.Json.JsonSerializer.Serialize(AuthoredMessages.issues ())
            |> System.Text.Encoding.UTF8.GetBytes
            yield!
                LocalizationPolicy.languages
                |> List.collect (fun language ->
                    [
                        chromePath
                        |> Option.map (fun path ->
                            Path.Combine(
                                Path.GetDirectoryName path |> nonNull,
                                "catalog-" + language + ".ts"
                            )),
                        MessageGeneration.languageSource language
                        chromePath
                        |> Option.map (fun _ ->
                            Path.GetFullPath(
                                "src/ClaimCore.Cli/Localization/Runtime/" + language + ".json"
                            )),
                        MessageGeneration.cliSource language
                    ])
        ]

    for issue in AuthoredMessages.issues () do
        eprintfn "Translation %s: %s %s (English fallback)." issue.Reason issue.Language issue.Key

    if checking then
        let referenceMatches =
            documentationPath
            |> Option.forall (fun path ->
                File.Exists path && File.ReadAllBytes path = documentation)

        let chromeMatches =
            chromePath
            |> Option.forall (fun path -> File.Exists path && File.ReadAllBytes path = chrome)

        let auxiliaryMatches =
            auxiliary
            |> List.forall (fun (path, bytes) ->
                path
                |> Option.forall (fun path -> File.Exists path && File.ReadAllBytes path = bytes))

        if
            (AuthoredMessages.requiredIssues ()).IsEmpty
            && ArtifactPublication.check output artifacts
            && referenceMatches
            && chromeMatches
            && auxiliaryMatches
        then
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

        for path, bytes in auxiliary do
            path |> Option.iter (fun path -> File.WriteAllBytes(path, bytes))

        documentationPath
        |> Option.iter (fun path ->
            Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
            File.WriteAllBytes(path, documentation))

        0
