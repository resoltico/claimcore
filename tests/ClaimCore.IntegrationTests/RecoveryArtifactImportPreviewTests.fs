module ClaimCore.IntegrationTests.RecoveryArtifactImportPreviewTests

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.RecoveryArtifactExportTestSupport

let private requireSignedPreview
    (core: IActorClaimsCore)
    (request: CommandRequest)
    (exported: RecoveryExport)
    =
    match
        core.Recovery.PreviewEnvelopeImport(exported.Bytes, CancellationToken.None)
        |> await
    with
    | RecoveryQueryOutcome.RecoverySucceeded value ->
        Expect.equal value.DecodedEffect.OperationId request.OperationId "Exact operation"
        Expect.equal value.DecodedEffect.CaseReference request.CaseReference "Exact reference"
        Expect.notEqual value.CaseId Guid.Empty "Authenticated case identity"
    | _ -> failtest "Current owner must preview its signed artifact."

let private requireIdempotentRetain
    (core: IActorClaimsCore)
    (request: CommandRequest)
    (exported: RecoveryExport)
    =
    let digest = SHA256.HashData(exported.Bytes) |> Convert.ToHexStringLower

    match
        core.Recovery.RetainEnvelopeImport(exported.Bytes, digest, CancellationToken.None)
        |> await
    with
    | RecoveryImportRetainOutcome.ExistingPreparation details ->
        Expect.equal details.Summary.OperationId request.OperationId "Existing exact preparation"
    | _ -> failtest "An authorized exact import must not duplicate retained authority."

let private requirePendingList (core: IActorClaimsCore) operationId =
    match
        core.Recovery.List(RecoveryListView.Pending, None, 10, CancellationToken.None)
        |> await
    with
    | RecoveryQueryOutcome.RecoverySucceeded page ->
        Expect.isTrue
            (page.Items
             |> List.exists (function
                 | RecoveryListItem.RetainedRecoveryItem summary ->
                     summary.OperationId = operationId
                 | _ -> false))
            "Installation recovery operator sees its pending exact preparation"
    | RecoveryQueryOutcome.RecoveryRejected _ -> failtest "Pending recovery list was refused."
    | _ -> failtest "Pending recovery list must be available."

let private requireAccepted (core: IActorClaimsCore) request =
    match core.Execute(request, CancellationToken.None) |> await with
    | SubmissionOutcome.Completed(_, _, DefiniteExecution.Accepted _, _) -> ()
    | SubmissionOutcome.RejectedBeforeAttempt _ ->
        failtest "Authorized prepared submit was refused."
    | SubmissionOutcome.FailedBeforeAttempt _ -> failtest "Authorized prepared submit failed."
    | _ -> failtest "Authorized prepared submit must accept once."

let private actorPreview () =
    withAuthorityRuntimeDatabase (fun ownerConnection app writer witness ->
        let owner = human "artifact-preview-owner"
        let stranger = human "artifact-preview-stranger"
        provision ownerConnection witness owner |> applied
        use source = RuntimeDataSource.create app
        let registry = new ActorGrantRegistry(source, witness)
        registry.RegisterActor(owner, stranger) |> await |> applied
        grant source witness owner Role.CaseEditor
        grant source witness owner Role.RecoveryExporter
        grant source witness owner Role.RecoveryOperator

        use runtime =
            Runtime.OpenPostgres(
                app,
                writer,
                witnessKey (),
                suppressionKeyFile (),
                artifactKeyRingFile (),
                CancellationToken.None
            )
            |> await
            |> accepted

        let request =
            openRequest (Guid.NewGuid()) ("PREVIEW-" + Guid.NewGuid().ToString("N"))

        let core = runtime.ForActor owner

        let digest =
            match core.Prepare(request, CancellationToken.None) |> await with
            | PrepareOutcome.Prepared(details, _) ->
                details.Summary.RequestSha256
                |> Option.defaultWith (fun () -> failtest "Digest missing.")
            | _ -> failtest "Synthetic owner preparation must be retained."

        let exported =
            match
                core.Recovery.ExportEnvelope(request.OperationId, digest, CancellationToken.None)
                |> await
            with
            | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found value) -> value
            | _ -> failtest "Synthetic owner export must be witnessed."

        requirePendingList core request.OperationId
        requireSignedPreview core request exported
        requireIdempotentRetain core request exported

        requireAccepted core request

        match
            (runtime.ForActor stranger)
                .Recovery.PreviewEnvelopeImport(exported.Bytes, CancellationToken.None)
            |> await
        with
        | RecoveryQueryOutcome.RecoveryRejected RecoveryRejection.ResourceUnavailable -> ()
        | _ -> failtest "Unscoped actor cannot preview claimant-bearing artifact.")


let tests =
    testList
        "signed recovery artifact import preview"
        [
            testCase
                "[CC-AUTH-001] signed import preview requires current actor authority"
                actorPreview
        ]
