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
        invalidArg
            (nameof arguments)
            "Use --output <directory>, --check [--output <directory>], or --check-translations."

let private prepareBrowserCatalogs output =
    let required = AuthoredMessages.requiredIssues ()

    if not required.IsEmpty then
        for issue in required do
            eprintfn "Browser translation %s: %s %s." issue.Reason issue.Language issue.Key

        1
    else
        CatalogValidation.validate () |> ignore
        let files = Path.Combine(output, "assets", "catalogs")
        Directory.CreateDirectory(files) |> ignore

        let manifest =
            AuthoredMessages.all ()
            |> Map.map (fun language entries ->
                let bytes = LocalizationPolicy.browserCatalog entries
                let hash = Security.Cryptography.SHA256.HashData bytes |> Convert.ToHexStringLower
                let name = language + "." + hash + ".json"
                File.WriteAllBytes(Path.Combine(files, name), bytes)
                "/assets/catalogs/" + name)

        File.WriteAllBytes(
            Path.Combine(output, "catalog-manifest.json"),
            Text.Json.JsonSerializer.SerializeToUtf8Bytes manifest
        )

        File.WriteAllBytes(
            Path.Combine(output, "catalog-sources.json"),
            Text.Json.JsonSerializer.SerializeToUtf8Bytes(AuthoredMessages.browserSources ())
        )

        0

[<EntryPoint>]
let main arguments =
    if arguments.Length = 2 && arguments[0] = "--prepare-browser-catalogs" then
        prepareBrowserCatalogs (Path.GetFullPath arguments[1])
    elif arguments = [| "--check-translations" |] then
        let issues = AuthoredMessages.issues ()

        for issue in issues do
            eprintfn "Release translation %s: %s %s." issue.Reason issue.Language issue.Key

        if LocalizationPolicy.releaseReady issues then 0 else 1
    else
        let output, checking = invocation arguments
        let artifacts = ContractArtifacts.all ()

        for issue in AuthoredMessages.issues () do
            eprintfn
                "Translation %s: %s %s (English fallback)."
                issue.Reason
                issue.Language
                issue.Key

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
