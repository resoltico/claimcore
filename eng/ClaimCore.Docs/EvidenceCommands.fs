namespace ClaimCore.Docs

open System

[<RequireQualifiedAccess>]
module EvidenceCommands =
    let private loadCollectedStages (root: RepositoryRoot) (runId: string) attempt =
        let loaded =
            Stages.definitions
            |> List.map (EvidenceReconciliation.loadStage root runId attempt)

        match
            loaded
            |> List.tryPick (function
                | Error value -> Some value
                | _ -> None)
        with
        | Some error -> Error error
        | None ->
            Ok(
                loaded
                |> List.choose (function
                    | Ok value -> Some value
                    | _ -> None)
            )

    let private loadStages root runId attempt =
        StageCollection.collect root runId attempt
        |> Result.bind (fun () -> loadCollectedStages root runId attempt)
        |> Result.mapError (fun message ->
            if attempt > 1 then
                message
                + " Use Re-run all jobs: every producer must belong to this complete attempt."
            else
                message)

    let private stageIdentityError (source: SourceIdentity) (stages: StageManifest list) =
        if
            stages
            |> List.exists (fun stage -> stage.Output.SourceSha256 <> source.ContentSha256)
        then
            Some "Stage source identities do not match the current repository."
        elif
            stages
            |> List.exists (fun stage -> stage.Output.LocksSha256 <> source.LocksSha256)
        then
            Some "Stage lock identities do not match the current repository."
        elif
            List.zip Stages.definitions stages
            |> List.exists (fun (definition, stage) ->
                definition.EvidencePlatform |> Option.exists ((<>) stage.Platform))
        then
            Some "A stage was produced on the wrong platform for required CI evidence."
        else
            None

    let private collect parsed =
        match
            parsed
            |> List.tryPick (function
                | Error value -> Some value
                | _ -> None)
        with
        | Some error -> Error error
        | None ->
            Ok(
                parsed
                |> List.choose (function
                    | Ok value -> Some value
                    | _ -> None)
            )

    let private loadReports (root: RepositoryRoot) (stages: StageManifest list) =
        let inputs = stages |> List.find (fun stage -> stage.StageId = "evidence-inputs")

        let expectedPaths =
            Stages.testReports
            |> List.map (fun report -> report.StageId + "/" + report.FileName)
            |> Set.ofList

        let actualPaths =
            inputs.Output.Files
            |> List.map _.Path
            |> List.filter (fun path -> path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase))
            |> Set.ofList

        if actualPaths <> expectedPaths then
            Error "Evidence input contains missing or unexpected TRX files."
        else
            Stages.testReports
            |> List.map (fun expected ->
                EvidenceReconciliation.trxPath root inputs expected
                |> Result.bind (fun path ->
                    let producer =
                        stages |> List.find (fun stage -> stage.StageId = expected.StageId)

                    match
                        producer.Output.Files
                        |> List.filter (fun file ->
                            file.Path.EndsWith(expected.FileName, StringComparison.Ordinal))
                    with
                    | [ file ] when Repository.sha256File path = file.Sha256 -> Ok path
                    | [ _ ] -> Error "Downloaded TRX bytes differ from their producer manifest."
                    | _ -> Error "Producer manifest does not identify exactly one expected TRX.")
                |> Result.bind (Evidence.parseTrx expected))
            |> collect

    let private contractEvidence (root: RepositoryRoot) (reports: TestReport list) =
        match MarkdownModel.readAll root with
        | Error found -> Error(found |> List.map _.Message |> String.concat " ")
        | Ok documents ->
            match Contracts.declarations documents with
            | Error found -> Error(found |> List.map _.Message |> String.concat " ")
            | Ok declarations ->
                match Reviews.verify root declarations (DateOnly.FromDateTime(DateTime.UtcNow)) with
                | Error found -> Error(found |> List.map _.Message |> String.concat " ")
                | Ok reviews ->
                    EvidenceReconciliation.contracts declarations reports
                    |> Result.map (fun contracts -> contracts, reviews)
                    |> Result.mapError (String.concat " ")

    let private downloadedAssurance (root: RepositoryRoot) (stages: StageManifest list) =
        [
            "coverage", "artifacts/assurance-inputs/coverage"
            "container-sbom", "artifacts/assurance-inputs/container-sbom"
        ]
        |> List.map (fun (stageId, relative) ->
            let producer = stages |> List.find (fun stage -> stage.StageId = stageId)

            Repository.registeredPath root relative
            |> Result.bind (Repository.ensureExistingSafe root)
            |> Result.bind (fun path -> PublishManifest.verifyTree path producer.Output))
        |> collect
        |> Result.map ignore

    let evidence (root: RepositoryRoot) (runner: IProcessRunner) (runId: string) attempt =
        if not (Stages.validRunId runId) || attempt < 1 then
            Error "The evidence run identity is invalid."
        else
            match Provenance.sourceIdentity root runner, loadStages root runId attempt with
            | Error message, _
            | _, Error message -> Error message
            | Ok source, Ok stages ->
                match stageIdentityError source stages with
                | Some message -> Error message
                | None ->
                    downloadedAssurance root stages
                    |> Result.bind (fun () -> StructuredReports.validate root stages)
                    |> Result.bind (fun () ->
                        loadReports root stages
                        |> Result.bind (fun reports ->
                            contractEvidence root reports
                            |> Result.bind (fun (contracts, reviews) ->
                                let result =
                                    {
                                        Stages = stages
                                        Reports = reports
                                        Contracts = contracts
                                        Reviews = reviews
                                    }

                                EvidenceReport.write root runId attempt source result
                                |> Result.map (fun () -> result))))
