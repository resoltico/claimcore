namespace ClaimCore.Docs

open System
open System.IO

[<RequireQualifiedAccess>]
module LocalFrontendReports =
    let private read (root: RepositoryRoot) relative =
        Repository.registeredPath root relative
        |> Result.bind (Repository.ensureExistingSafe root)
        |> Result.bind (fun path ->
            try
                if FileInfo(path).Length > 16L * 1024L * 1024L then
                    Error "Frontend test report exceeds its bounded size."
                else
                    Ok(File.ReadAllBytes(path))
            with error ->
                Error $"Frontend test report could not be read ({error.GetType().Name}).")

    let verifyVitest root =
        read root "artifacts/frontend/vitest-summary.json"
        |> Result.bind StructuredReports.validateVitestBytes
        |> Result.mapError (fun message -> "Vitest: " + message)

    let verifyBrowser root engine =
        match engine with
        | "chromium"
        | "firefox"
        | "webkit" ->
            read root $"artifacts/browser/{engine}.json"
            |> Result.bind (StructuredReports.validateBrowserBytes engine)
            |> Result.mapError (fun message -> engine + ": " + message)
        | _ -> Error "Browser report engine is not registered."

    let verifyAll root =
        [
            verifyVitest root
            verifyBrowser root "chromium"
            verifyBrowser root "firefox"
            verifyBrowser root "webkit"
        ]
        |> List.tryPick (function
            | Error message -> Some message
            | Ok() -> None)
        |> function
            | Some message -> Error message
            | None -> Ok()
