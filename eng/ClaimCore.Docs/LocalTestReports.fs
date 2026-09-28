namespace ClaimCore.Docs

open System.IO

[<RequireQualifiedAccess>]
module LocalTestReports =
    let internal verifyDefinition
        (root: RepositoryRoot)
        (expected: TestReportDefinition)
        (relative: string)
        =
        if Path.GetFileName(relative) <> expected.FileName then
            Error "The local TRX filename does not match its registered assembly."
        else
            Repository.registeredPath root relative
            |> Result.bind (Repository.ensureExistingSafe root)
            |> Result.bind (Evidence.parseTrx expected)
            |> Result.map ignore

    let verify root assembly relative =
        match Stages.testReports |> List.tryFind (fun report -> report.Assembly = assembly) with
        | None -> Error "The local TRX assembly is not registered."
        | Some expected -> verifyDefinition root expected relative
