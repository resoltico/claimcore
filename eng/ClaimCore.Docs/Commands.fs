namespace ClaimCore.Docs

open System

[<RequireQualifiedAccess>]
module Commands =
    let private show (diagnostics: Diagnostic list) =
        for diagnostic in diagnostics do
            let location =
                match diagnostic.Path, diagnostic.Line with
                | Some path, Some line -> $"{path}:{line}: "
                | Some path, None -> path + ": "
                | _ -> ""

            Console.Error.WriteLine(location + diagnostic.Message)

    let private documentation (operation: Result<DocumentationAssessment, Diagnostic list>) =
        match operation with
        | Ok assessment ->
            printfn
                "Documentation passed: %d documents, %d links, %d contracts."
                assessment.Documents.Length
                assessment.Links
                assessment.Contracts.Length

            ExitCode.Success
        | Error diagnostics ->
            show diagnostics
            ExitCode.CheckFailed

    let run (root: RepositoryRoot) (runner: IProcessRunner) (arguments: string list) =
        match arguments with
        | [ "check" ] -> DocumentationCommands.check root runner |> documentation
        | [ "write" ] -> DocumentationCommands.write root runner |> documentation
        | _ ->
            Console.Error.WriteLine("Usage: ClaimCore.Docs check | write")
            ExitCode.InvocationFailed
