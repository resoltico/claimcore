module ClaimCore.Tests.ReceiptFirstTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private clock =
    { new IBusinessDate with
        member _.Today() = today
    }

let private draft operationId reference =
    {
        OperationId = operationId
        CaseReference = reference
        ExpectedVersion = 0L
        Kind = CommandKind.Open
        Values =
            [
                "incidentDate", registration.IncidentDate
                "incidentNotificationDate", registration.IncidentNotificationDate
                "incidentCountry", registration.IncidentCountry
                "claimantName", registration.ClaimantName
                "insurerName", registration.InsurerName
                "claimedAmount", registration.ClaimedAmount
                "claimedCurrency", registration.ClaimedCurrency
            ]
    }

let private await (operation: Threading.Tasks.Task<'value>) = operation.GetAwaiter().GetResult()

let private acceptDirectly (claims: CoreStore.Store) input =
    let request =
        Drafts.bind input
        |> Result.defaultWith (fun _ -> failtest "Synthetic draft must bind.")

    match Service.executeAsync claims clock request |> await with
    | Ok receipt -> receipt
    | Error _ -> failtest "Synthetic direct command must accept."

let private core claims recovery =
    CoreApi.create (claims :> IClaimStore) (recovery :> IRecoveryStore) clock

type private LostConfirmationStore() =
    let inner = new CoreStore.Store()
    let mutable loseFirst = true

    member _.TransactionCalls = inner.TransactionCalls

    interface IClaimStore with
        member _.Transact(operation, decide) =
            task {
                let! result = (inner :> IClaimStore).Transact(operation, decide)

                match result with
                | Ok _ when loseFirst ->
                    loseFirst <- false
                    let operationId = (Operation.request operation).OperationId
                    return Error(CoreFailure.CommitOutcomeUnknown operationId)
                | _ -> return result
            }

        member _.Get reference = (inner :> IClaimStore).Get reference
        member _.List after = (inner :> IClaimStore).List after

        member _.History(reference, afterVersion) =
            (inner :> IClaimStore).History(reference, afterVersion)

        member _.Operation operationId =
            (inner :> IClaimStore).Operation operationId

        member _.Accepted(operationId, requestSha256) =
            (inner :> IClaimStore).Accepted(operationId, requestSha256)

let private acceptedWithoutPreparation =
    testCase
        "[CC-APP-002] accepted replay needs no retained preparation or recovery read"
        (fun () ->
            let claims = new CoreStore.Store()

            let recovery =
                new CoreRecoveryStore.Store(getFailure = RecoveryStoreFailure.StoreUnavailable)

            let input = draft (Guid.NewGuid()) "RECEIPT-FIRST"
            let original = acceptDirectly claims input
            let runtime = core claims recovery

            match runtime.Prepare(input, CancellationToken.None) |> await with
            | PrepareOutcome.ObservedAccepted receipt ->
                Expect.equal receipt.OperationId original.OperationId "Exact operation identity"
                Expect.isTrue receipt.Replayed "Accepted history is replayed"
            | _ -> failtest "Prepare must observe accepted history before recovery."

            match runtime.Execute(input, CancellationToken.None) |> await with
            | SubmissionOutcome.ObservedAccepted receipt ->
                Expect.equal receipt.Snapshot.Version 1L "No second revision"
            | _ -> failtest "Execute must observe accepted history before recovery."

            Expect.equal recovery.GetCalls 0 "Recovery need not be available for accepted replay"
            Expect.equal claims.TransactionCalls 1 "Replay never starts a new claim transaction")

let private acceptedConflict =
    testCase
        "[CC-APP-002] accepted identity conflict refuses without recovery disclosure"
        (fun () ->
            let claims = new CoreStore.Store()

            let recovery =
                new CoreRecoveryStore.Store(getFailure = RecoveryStoreFailure.StoreUnavailable)

            let input = draft (Guid.NewGuid()) "RECEIPT-CONFLICT"
            acceptDirectly claims input |> ignore

            let conflicting =
                { input with
                    CaseReference = "OTHER-REFERENCE"
                }

            let runtime = core claims recovery

            match runtime.Prepare(conflicting, CancellationToken.None) |> await with
            | PrepareOutcome.PrepareRejected(_, rejection) ->
                Expect.equal rejection.Code RejectionCode.IdempotencyConflict "Identity conflict"
            | _ -> failtest "Prepare must not disclose a different accepted receipt."

            match runtime.Execute(conflicting, CancellationToken.None) |> await with
            | SubmissionOutcome.RejectedBeforeAttempt(None, rejection) ->
                Expect.equal rejection.Code RejectionCode.IdempotencyConflict "Identity conflict"
            | _ -> failtest "Execute must not disclose a different accepted receipt."

            Expect.equal recovery.GetCalls 0 "Conflicts never consult technical preparations"
            Expect.equal claims.TransactionCalls 1 "Conflict never starts a new claim transaction")

let private cancellationAfterObservation =
    testCase
        "[CC-APP-002] cancellation after accepted observation preserves definite receipt"
        (fun () ->
            use preparationCancellation = new CancellationTokenSource()
            use executionCancellation = new CancellationTokenSource()
            let mutable current = preparationCancellation
            let claims = new CoreStore.Store(onAccepted = (fun () -> current.Cancel()))
            let recovery = new CoreRecoveryStore.Store()
            let input = draft (Guid.NewGuid()) "RECEIPT-CANCEL"
            acceptDirectly claims input |> ignore
            let runtime = core claims recovery

            match runtime.Prepare(input, preparationCancellation.Token) |> await with
            | PrepareOutcome.ObservedAccepted _ -> ()
            | _ -> failtest "Observed acceptance must not be relabelled cancellation."

            current <- executionCancellation

            match runtime.Execute(input, executionCancellation.Token) |> await with
            | SubmissionOutcome.ObservedAccepted _ -> ()
            | _ -> failtest "Observed acceptance must not be relabelled cancellation."

            Expect.equal recovery.GetCalls 0 "Definite receipts bypass recovery")

let private lostCommitConfirmation =
    testCase
        "[CC-APP-002] accepted receipt wins after original commit confirmation is lost"
        (fun () ->
            let claims = new LostConfirmationStore()
            let recovery = new CoreRecoveryStore.Store()
            let runtime = CoreApi.create claims recovery clock
            let input = draft (Guid.NewGuid()) "RECEIPT-UNCERTAIN"

            match runtime.Execute(input, CancellationToken.None) |> await with
            | SubmissionOutcome.AttemptUnresolved(_, _, fault) ->
                Expect.equal
                    fault.Code
                    FaultCode.CommitOutcomeUnknown
                    "Original result is uncertain"
            | _ -> failtest "Lost commit confirmation must remain explicitly uncertain."

            let recoveryReads = recovery.GetCalls

            match runtime.Execute(input, CancellationToken.None) |> await with
            | SubmissionOutcome.ObservedAccepted receipt ->
                Expect.equal receipt.Snapshot.Version 1L "One accepted revision"
            | _ -> failtest "Exact retry must find accepted history before recovery."

            Expect.equal
                recovery.GetCalls
                recoveryReads
                "No recovery read after receipt observation"

            Expect.equal claims.TransactionCalls 1 "Exact retry does not submit again")

let private acceptedReadFailure =
    testCase
        "[CC-APP-002] accepted-history read failure refuses before fresh preparation"
        (fun () ->
            let claims = new CoreStore.Store(acceptedFailure = CoreFailure.StoreUnavailable)
            let recovery = new CoreRecoveryStore.Store()
            let runtime = core claims recovery
            let input = draft (Guid.NewGuid()) "RECEIPT-FAULT"

            match runtime.Prepare(input, CancellationToken.None) |> await with
            | PrepareOutcome.PrepareFailed(_, fault) ->
                Expect.equal fault.Code FaultCode.StoreUnavailable "Fail closed"
            | _ -> failtest "Prepare must not treat failed receipt read as missing."

            match runtime.Execute(input, CancellationToken.None) |> await with
            | SubmissionOutcome.FailedBeforeAttempt(None, fault) ->
                Expect.equal fault.Code FaultCode.StoreUnavailable "Fail closed"
            | _ -> failtest "Execute must not treat failed receipt read as missing."

            Expect.equal recovery.GetCalls 0 "No technical preparation read"
            Expect.equal claims.TransactionCalls 0 "No claim execution")

let private retainedIdentityPrivacy =
    testCase
        "[CC-REC-001] mismatched retained identity refuses without preparation metadata"
        (fun () ->
            let claims = new CoreStore.Store()
            let recovery = new CoreRecoveryStore.Store()
            let runtime = core claims recovery
            let input = draft (Guid.NewGuid()) "RETAINED-PRIVATE"

            let digest =
                match runtime.Prepare(input, CancellationToken.None) |> await with
                | PrepareOutcome.Prepared(details, _) ->
                    details.Summary.RequestSha256
                    |> Option.defaultWith (fun () -> failtest "Synthetic digest is required.")
                | _ -> failtest "Synthetic request must prepare."

            let wrongDigest = (if digest[0] = 'a' then "b" else "a") + digest.Substring(1)

            match
                runtime.Recovery.Resolve(input.OperationId, wrongDigest, CancellationToken.None)
                |> await
            with
            | ResolveOutcome.RefusedBeforeAttempt(None, rejection) ->
                Expect.equal
                    rejection.Code
                    RecoveryRejectionCode.SourceDigestMismatch
                    "Digest conflict"
            | _ -> failtest "Wrong digest must not disclose retained preparation."

            match
                runtime.Recovery.Dismiss(
                    input.OperationId,
                    wrongDigest,
                    true,
                    CancellationToken.None
                )
                |> await
            with
            | RecoveryDismissOutcome.DismissRefused(None, rejection) ->
                Expect.equal
                    rejection.Code
                    RecoveryRejectionCode.SourceDigestMismatch
                    "Digest conflict"
            | _ -> failtest "Wrong digest must not disclose retained preparation details."

            let conflicting =
                { input with
                    CaseReference = "OTHER-RETAINED"
                }

            match runtime.Execute(conflicting, CancellationToken.None) |> await with
            | SubmissionOutcome.RejectedBeforeAttempt(None, rejection) ->
                Expect.equal rejection.Code RejectionCode.IdempotencyConflict "Request conflict"
            | _ -> failtest "Wrong request bytes must not disclose retained preparation.")

let tests =
    testList
        "accepted receipt first"
        [
            acceptedWithoutPreparation
            acceptedConflict
            cancellationAfterObservation
            lostCommitConfirmation
            acceptedReadFailure
            retainedIdentityPrivacy
        ]
