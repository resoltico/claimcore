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

    let private stage
        (root: RepositoryRoot)
        (runner: IProcessRunner)
        (stageId: string)
        (runId: string)
        (rawAttempt: string)
        (outcome: string)
        (started: string)
        (finished: string)
        (output: string)
        =
        match Int32.TryParse(rawAttempt) with
        | false, _ ->
            Console.Error.WriteLine("Stage attempt must be an integer.")
            ExitCode.InvocationFailed
        | true, attempt ->
            match
                StageCommands.stageManifest
                    root
                    runner
                    stageId
                    runId
                    attempt
                    outcome
                    started
                    finished
                    output
            with
            | Ok() ->
                printfn "Recorded stage manifest: %s" stageId
                ExitCode.Success
            | Error message ->
                Console.Error.WriteLine(message)
                ExitCode.CheckFailed

    let private publish
        (root: RepositoryRoot)
        (runner: IProcessRunner)
        (stageId: string)
        (output: string)
        (manifest: string)
        =
        match StageCommands.verifyPublishManifest root runner stageId output manifest with
        | Ok() ->
            printfn "Publish manifest verified: %s" stageId
            ExitCode.Success
        | Error message ->
            Console.Error.WriteLine(message)
            ExitCode.CheckFailed

    let private evidence
        (root: RepositoryRoot)
        (runner: IProcessRunner)
        (runId: string)
        (rawAttempt: string)
        =
        match Int32.TryParse(rawAttempt) with
        | false, _ ->
            Console.Error.WriteLine("Evidence attempt must be an integer.")
            ExitCode.InvocationFailed
        | true, attempt ->
            let assessed =
                try
                    EvidenceCommands.evidence root runner runId attempt
                with error ->
                    Error($"Evidence validation failed unexpectedly ({error.GetType().Name}).")

            match assessed with
            | Ok result ->
                printfn
                    "Evidence passed: %d stages, %d reports, %d contracts."
                    result.Stages.Length
                    result.Reports.Length
                    result.Contracts.Length

                ExitCode.Success
            | Error message ->
                let recorded =
                    EvidenceReport.writeFailure
                        root
                        runId
                        attempt
                        (Provenance.sourceIdentity root runner |> Result.toOption)
                        message

                match recorded with
                | Ok() ->
                    Console.Error.WriteLine(message)
                    ExitCode.CheckFailed
                | Error _ ->
                    Console.Error.WriteLine("The evidence failure report could not be written.")
                    ExitCode.Unexpected

    let private convergence root arguments =
        match arguments with
        | [ "inventory" ] ->
            Console.Out.Write(ConvergenceInventory.render ())
            ExitCode.Success
        | [ "check"; baseline; lineage; matrix ] ->
            match ConvergenceAssurance.check root baseline lineage matrix with
            | Ok message ->
                Console.Out.WriteLine(message)
                ExitCode.Success
            | Error message ->
                Console.Error.WriteLine(message)
                ExitCode.CheckFailed
        | _ ->
            Console.Error.WriteLine(
                "Usage: ClaimCore.Docs convergence inventory | convergence check <baseline> <lineage> <matrix>"
            )

            ExitCode.InvocationFailed

    let run (root: RepositoryRoot) (runner: IProcessRunner) (arguments: string list) =
        match arguments with
        | [ "check" ] -> DocumentationCommands.check root runner |> documentation
        | [ "write" ] -> DocumentationCommands.write root runner |> documentation
        | [ "stage-manifest"; stageId; runId; attempt; outcome; started; finished; output ] ->
            stage root runner stageId runId attempt outcome started finished output
        | [ "verify-publish-manifest"; stageId; output; manifest ] ->
            publish root runner stageId output manifest
        | [ "evidence"; runId; attempt ] -> evidence root runner runId attempt
        | "convergence" :: rest -> convergence root rest
        | _ ->
            Console.Error.WriteLine(
                "Usage: ClaimCore.Docs check | write | stage-manifest <stage-id> <run-id> <attempt> <outcome> <started-utc> <finished-utc> <output-root> | verify-publish-manifest <stage-id> <output-root> <manifest> | evidence <run-id> <attempt>"
            )

            ExitCode.InvocationFailed
