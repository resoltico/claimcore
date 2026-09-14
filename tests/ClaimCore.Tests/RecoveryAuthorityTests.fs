module ClaimCore.Tests.RecoveryAuthorityTests

open System
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private await (task: System.Threading.Tasks.Task<'value>) = task.GetAwaiter().GetResult()

let private draft operationId claimantName : CommandDraft =
    {
        OperationId = operationId
        CaseReference = "REC-AUTHORITY-" + operationId.ToString("N")
        ExpectedVersion = 0L
        Command =
            DraftCommand.Flat(
                CommandKind.Open,
                [
                    "incidentDate", registration.IncidentDate
                    "incidentNotificationDate", registration.IncidentNotificationDate
                    "incidentCountry", registration.IncidentCountry
                    "claimantName", claimantName
                    "insurerName", registration.InsurerName
                    "claimedAmount", registration.ClaimedAmount
                    "claimedCurrency", registration.ClaimedCurrency
                ]
            )
    }

let private create () =
    let claims = new CoreStore.Store()
    let recovery = new CoreRecoveryStore.Store()
    recovery.AttachClaimStore(claims :> IClaimStore)

    CoreApi.create (claims :> IClaimStore) (recovery :> IRecoveryStore) (businessTime today),
    recovery

let private durableRevocationClosesExactIdentity =
    testCase
        "[CC-REC-001] durable revocation closes exact identity before and after preparation pruning"
        (fun () ->
            let core, recovery = create ()
            let operationId = Guid.NewGuid()
            let original = draft operationId registration.ClaimantName

            let digest =
                match core.Prepare(boundRequest original, CancellationToken.None) |> await with
                | PrepareOutcome.Prepared(details, _) ->
                    details.Summary.RequestSha256
                    |> Option.defaultWith (fun () ->
                        failtest "Prepared request must retain its digest.")
                | _ -> failtest "Synthetic request must be retained before revocation."

            match
                core.Recovery.Dismiss(operationId, digest, true, CancellationToken.None)
                |> await
            with
            | RecoveryDismissOutcome.DismissedPreparation _ -> ()
            | _ -> failtest "Dismissal must record durable operation closure."

            match core.Execute(boundRequest original, CancellationToken.None) |> await with
            | SubmissionOutcome.RejectedBeforeAttempt(_, rejection) ->
                Expect.equal
                    rejection.Code
                    RejectionCode.OperationRevoked
                    "Exact identity is closed"
            | _ -> failtest "An exact revoked operation must not be admitted for execution."

            let changed = draft operationId (registration.ClaimantName + " Changed")

            match core.Execute(boundRequest changed, CancellationToken.None) |> await with
            | SubmissionOutcome.RejectedBeforeAttempt(_, rejection) ->
                Expect.equal
                    rejection.Code
                    RejectionCode.IdempotencyConflict
                    "Different bytes never disclose or override a revoked identity"
            | _ ->
                failtest
                    "Changed content under a revoked operation ID must be an identity conflict."

            recovery.PruneRetainedForTest operationId

            match core.Recovery.Resolve(operationId, digest, CancellationToken.None) |> await with
            | ResolveOutcome.RefusedBeforeAttempt(None, rejection) ->
                Expect.equal
                    rejection.Code
                    RecoveryRejectionCode.OperationRevoked
                    "Tombstone-only exact recovery remains terminal"
            | _ -> failtest "Pruning evidence must not resurrect a revoked operation.")

let private viewMismatchIsRejected (core: IClaimsCore) operationId =
    let cursor =
        RecoveryCursorCodec.encode
            {
                View = RecoveryListView.Pending
                OccurredAt = DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero)
                OperationId = operationId
            }

    match
        core.Recovery.List(RecoveryListView.Terminal, Some cursor, 1, CancellationToken.None)
        |> await
    with
    | RecoveryQueryOutcome.RecoveryRejected rejection ->
        Expect.equal rejection.Code RecoveryRejectionCode.InvalidRecoveryInput "Cursor view binding"
    | _ -> failtest "A pending cursor must not navigate a terminal recovery view."

let private crossOperationAttemptCursorIsRejected
    (core: IClaimsCore)
    sourceOperation
    targetOperation
    =
    let cursor =
        RecoveryAttemptCursorCodec.encode
            {
                OperationId = sourceOperation
                StartedAt = DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero)
                AttemptId = Guid.NewGuid()
            }

    match
        core.Recovery.Inspect(targetOperation, Some cursor, 1, CancellationToken.None)
        |> await
    with
    | RecoveryQueryOutcome.RecoveryRejected rejection ->
        Expect.equal
            rejection.Code
            RecoveryRejectionCode.InvalidRecoveryInput
            "Attempt cursor operation binding"
    | _ -> failtest "An attempt cursor must not navigate another operation's evidence."

let private cursorsAreBoundToTheirAuthorityScope =
    testCase
        "[CC-REC-001] recovery list and attempt cursors reject a different view or operation"
        (fun () ->
            let core, _ = create ()
            let firstOperation = Guid.NewGuid()
            let secondOperation = Guid.NewGuid()
            viewMismatchIsRejected core firstOperation
            crossOperationAttemptCursorIsRejected core firstOperation secondOperation)

let tests =
    testList
        "recovery authority closure"
        [ durableRevocationClosesExactIdentity; cursorsAreBoundToTheirAuthorityScope ]
