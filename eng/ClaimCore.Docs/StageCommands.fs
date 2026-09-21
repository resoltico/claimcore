namespace ClaimCore.Docs

open System
open System.Globalization
open System.IO

[<RequireQualifiedAccess>]
module StageCommands =
    let private timestamp (value: string) =
        let mutable parsed = DateTimeOffset.MinValue

        if
            DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                &parsed
            )
        then
            Ok parsed
        else
            Error "Stage timestamps must use the round-trip ISO-8601 format."

    let private outputRoot (root: RepositoryRoot) (value: string) =
        let resolved =
            if Path.IsPathRooted(value) then
                Ok(Path.GetFullPath(value))
            else
                Repository.registeredPath root value

        match resolved with
        | Error message -> Error message
        | Ok path ->
            let relative = Repository.relativePath root path

            if not (relative.StartsWith("artifacts/", StringComparison.Ordinal)) then
                Error "Stage output roots must be below artifacts/."
            else
                Repository.ensureExistingSafe root path
                |> Result.map (fun safe -> relative, safe)

    let private stageInputs
        (root: RepositoryRoot)
        (stageId: string)
        (runId: string)
        attempt
        (outcome: string)
        (started: string)
        (finished: string)
        (output: string)
        =
        match Stages.tryFind stageId with
        | None -> Error $"Stage '{stageId}' is not registered."
        | Some _ when not (Stages.validRunId runId) -> Error "The evidence run ID is invalid."
        | Some _ when attempt < 1 -> Error "The evidence attempt must be positive."
        | Some _ when
            not ([ "success"; "failure"; "cancelled"; "skipped" ] |> List.contains outcome)
            ->
            Error "The stage outcome is invalid."
        | Some definition when
            not (definition.AllowedPlatforms |> List.contains (Stages.currentPlatform ()))
            ->
            Error $"Stage '{stageId}' is not registered for this platform."
        | Some definition ->
            match timestamp started, timestamp finished, outputRoot root output with
            | Ok startTime, Ok finishTime, Ok(relative, safe) ->
                Ok(definition, startTime, finishTime, relative, safe)
            | Error message, _, _
            | _, Error message, _
            | _, _, Error message -> Error message

    let private createOutput
        (root: RepositoryRoot)
        (runner: IProcessRunner)
        (stageId: string)
        (safeOutput: string)
        =
        match Provenance.sourceIdentity root runner, Provenance.toolchain root runner with
        | Error message, _
        | _, Error message -> Error message
        | Ok source, Ok toolchain ->
            PublishManifest.create
                stageId
                source.ContentSha256
                source.LocksSha256
                toolchain
                safeOutput
            |> Result.map (fun publish -> source, publish)

    let private writeStage
        root
        runId
        attempt
        (definition: StageDefinition)
        outcome
        started
        finished
        relative
        (publish: PublishTreeManifest)
        =
        let platform = Stages.currentPlatform ()

        let missing =
            definition.RequiredOutputs
            |> List.filter (Stages.satisfies platform publish.Files >> not)

        if not missing.IsEmpty then
            Error(
                "Stage output is missing: "
                + (missing |> List.map Stages.displayRequirement |> String.concat ", ")
            )
        else
            let manifest =
                {
                    SchemaVersion = 2
                    StageId = definition.Id
                    RunId = runId
                    Attempt = attempt
                    Outcome = outcome
                    StartedUtc = started
                    FinishedUtc = finished
                    Platform = platform
                    Procedure = definition.Procedure
                    RequiredOutputs =
                        definition.RequiredOutputs |> List.map Stages.displayRequirement
                    OutputRoot = relative
                    Output = publish
                }

            AtomicFile.write
                root
                (EvidenceReconciliation.stageManifestPath runId attempt definition.Id)
                (StageManifestFormat.serialize manifest)
            |> Result.map ignore

    let private writePublishCopy
        root
        runId
        attempt
        (stageId: string)
        publish
        (previous: Result<unit, string>)
        =
        if not (stageId.StartsWith("publish-", StringComparison.Ordinal)) then
            previous
        else
            match previous with
            | Error message -> Error message
            | Ok() ->
                AtomicFile.write
                    root
                    $"artifacts/evidence/{runId}/{attempt}/publish/{stageId}.json"
                    (PublishManifestWriter.serialize publish)
                |> Result.map ignore

    // A successful producer must satisfy the same reviewed inventory as final reconciliation.
    // Failed producers retain their evidence without being relabelled as successful.
    let private validateTestReport root stageId outcome safeOutput (publish: PublishTreeManifest) =
        match
            outcome, Stages.testReports |> List.tryFind (fun report -> report.StageId = stageId)
        with
        | "success", Some expected ->
            let reports =
                publish.Files
                |> List.filter (fun file -> file.Path.EndsWith(".trx", StringComparison.Ordinal))

            match reports with
            | [ report ] when Path.GetFileName(report.Path) = expected.FileName ->
                Repository.ensureExistingSafe root (Path.Combine(safeOutput, report.Path))
                |> Result.bind (Evidence.parseTrx expected)
                |> Result.map ignore
                |> Result.mapError (fun message -> $"Stage '{stageId}': {message}")
            | _ ->
                Error
                    $"Stage '{stageId}' must contain exactly one TRX report named '{expected.FileName}'."
        | _ -> Ok()

    let stageManifest
        (root: RepositoryRoot)
        (runner: IProcessRunner)
        (stageId: string)
        (runId: string)
        attempt
        (outcome: string)
        (started: string)
        (finished: string)
        (output: string)
        =
        match stageInputs root stageId runId attempt outcome started finished output with
        | Error message -> Error message
        | Ok(definition, startedAt, finishedAt, relative, safeOutput) ->
            match createOutput root runner stageId safeOutput with
            | Error message -> Error message
            | Ok(_, publish) ->
                validateTestReport root stageId outcome safeOutput publish
                |> Result.bind (fun () ->
                    writeStage
                        root
                        runId
                        attempt
                        definition
                        outcome
                        startedAt
                        finishedAt
                        relative
                        publish)
                |> writePublishCopy root runId attempt stageId publish

    let private publishDefinition (stageId: string) =
        match Stages.tryFind stageId with
        | None -> Error $"Stage '{stageId}' is not registered."
        | Some _ when not (stageId.StartsWith("publish-", StringComparison.Ordinal)) ->
            Error "Only registered publication stages have publish-tree manifests."
        | Some definition -> Ok definition

    let private loadedPublish (root: RepositoryRoot) (path: string) =
        match Repository.registeredPath root path with
        | Error message -> Error message
        | Ok candidate ->
            Repository.ensureExistingSafe root candidate
            |> Result.bind (File.ReadAllBytes >> PublishManifest.parse)

    let verifyPublishManifest
        (root: RepositoryRoot)
        (runner: IProcessRunner)
        (stageId: string)
        (output: string)
        (manifestPath: string)
        =
        match
            publishDefinition stageId, outputRoot root output, loadedPublish root manifestPath
        with
        | Error message, _, _
        | _, Error message, _
        | _, _, Error message -> Error message
        | Ok _, Ok(_, safeOutput), Ok manifest ->
            match Provenance.sourceIdentity root runner with
            | Error message -> Error message
            | Ok _ when manifest.StageId <> stageId ->
                Error "Publish manifest has the wrong stage ID."
            | Ok source when
                manifest.SourceSha256 <> source.ContentSha256
                || manifest.LocksSha256 <> source.LocksSha256
                ->
                Error "Publish manifest provenance does not match the current repository."
            | Ok _ -> PublishManifest.verifyTree safeOutput manifest
