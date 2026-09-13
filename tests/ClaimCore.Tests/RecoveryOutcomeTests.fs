module ClaimCore.Tests.RecoveryOutcomeTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

type private ExecutionMode =
    | Normal
    | Throws
    | Unknown
    | FailsBeforeCommit

type private ScriptedClaimStore(mode: ExecutionMode) =
    let inner = new CoreStore.Store()
    member _.TransactionCalls = inner.TransactionCalls

    interface IClaimStore with
        member _.Transact(operation, decide) =
            match mode with
            | Normal -> (inner :> IClaimStore).Transact(operation, decide)
            | Throws ->
                Task.FromException<Result<Receipt, CoreFailure>>(InvalidOperationException())
            | Unknown ->
                let operationId = (Operation.request operation).OperationId
                Task.FromResult(Error(CoreFailure.CommitOutcomeUnknown operationId))
            | FailsBeforeCommit -> Task.FromResult(Error CoreFailure.StoreUnavailable)

        member _.Get reference = (inner :> IClaimStore).Get reference
        member _.List after = (inner :> IClaimStore).List after

        member _.History(reference, after) =
            (inner :> IClaimStore).History(reference, after)

        member _.Operation operationId =
            (inner :> IClaimStore).Operation operationId

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

let private prepared (core: IClaimsCore) operationId reference =
    match core.Prepare(draft operationId reference, CancellationToken.None).Result with
    | PrepareOutcome.Prepared(details, _) ->
        details.Summary.RequestSha256
        |> Option.defaultWith (fun () -> failtest "Retained request digest is required.")
    | _ -> failtest "Expected a retained preparation before resolution."

let private resolve (core: IClaimsCore) operationId digest token =
    core.Recovery.Resolve(operationId, digest, token).Result

let private makeCore mode (recovery: CoreRecoveryStore.Store) =
    let claims = new ScriptedClaimStore(mode)
    CoreApi.create (claims :> IClaimStore) (recovery :> IRecoveryStore) clock, claims

let private beforeAttemptCancellation =
    testCase
        "[CC-REC-001] cancellation before attempt admission leaves the claim untouched"
        (fun () ->
            use cancellation = new CancellationTokenSource()
            let mutable armed = false

            let recovery =
                new CoreRecoveryStore.Store(
                    onGet =
                        (fun () ->
                            if armed then
                                cancellation.Cancel())
                )

            let core, claims = makeCore Normal recovery
            let operationId = Guid.NewGuid()
            let digest = prepared core operationId "REC-BEFORE-ATTEMPT"
            armed <- true

            match resolve core operationId digest cancellation.Token with
            | ResolveOutcome.ResolveCancelledBeforeAttempt summary ->
                Expect.equal summary.OperationId operationId "Retained operation identity"
            | _ -> failtest "Cancellation before Start must remain definite."

            Expect.equal recovery.StartCalls 0 "No attempt was admitted"
            Expect.equal claims.TransactionCalls 0 "No claim transaction began"
            Expect.equal recovery.SettlementCalls 0 "No settlement was written")

let private afterAttemptCancellation =
    testCase
        "[CC-REC-001] cancellation after admitted attempt preserves definite acceptance"
        (fun () ->
            use cancellation = new CancellationTokenSource()
            let recovery = new CoreRecoveryStore.Store(onStart = cancellation.Cancel)
            let core, claims = makeCore Normal recovery
            let operationId = Guid.NewGuid()
            let digest = prepared core operationId "REC-AFTER-ATTEMPT"

            match resolve core operationId digest cancellation.Token with
            | ResolveOutcome.ResolveCompleted(_,
                                              _,
                                              DefiniteExecution.Accepted receipt,
                                              SettlementConfirmation.Confirmed) ->
                Expect.equal receipt.OperationId operationId "Accepted exact operation"
            | _ -> failtest "Caller cancellation cannot relabel an admitted commit."

            Expect.isTrue cancellation.IsCancellationRequested "Barrier was crossed"
            Expect.equal claims.TransactionCalls 1 "Claim transaction ran once"
            Expect.equal recovery.SettlementCalls 1 "Definite acceptance was settled")

let private admissionUnknown =
    testCase "[CC-REC-001] unknown attempt admission never executes the claim" (fun () ->
        let recovery =
            new CoreRecoveryStore.Store(
                startFailure = RecoveryStoreFailure.TechnicalMutationUnknown
            )

        let core, claims = makeCore Normal recovery
        let operationId = Guid.NewGuid()
        let digest = prepared core operationId "REC-ADMISSION-UNKNOWN"

        match resolve core operationId digest CancellationToken.None with
        | ResolveOutcome.ResolveAttemptAdmissionUnknown(summary, fault) ->
            Expect.equal summary.OperationId operationId "Retained operation identity"
            Expect.equal fault.Code FaultCode.TechnicalMutationUnknown "Unknown admission"
        | _ -> failtest "Unknown attempt admission must be explicit."

        Expect.equal claims.TransactionCalls 0 "No execution before confirmed admission"
        Expect.equal recovery.SettlementCalls 0 "No synthetic settlement")

let private unresolvedExecution mode expectedMessage =
    testCase expectedMessage (fun () ->
        let recovery = new CoreRecoveryStore.Store()
        let core, _ = makeCore mode recovery
        let operationId = Guid.NewGuid()

        let digest =
            prepared core operationId ("REC-UNRESOLVED-" + operationId.ToString("N"))

        match resolve core operationId digest CancellationToken.None with
        | ResolveOutcome.ResolveAttemptUnresolved(summary, attemptId, fault) ->
            Expect.equal summary.OperationId operationId "Retained operation identity"
            Expect.notEqual attemptId Guid.Empty "Durably admitted attempt identity"
            Expect.equal fault.Code FaultCode.CommitOutcomeUnknown "Execution uncertainty"
        | _ -> failtest "Unconfirmed execution must remain recoverable."

        Expect.equal recovery.StartCalls 1 "One confirmed attempt admission"
        Expect.equal recovery.SettlementCalls 0 "Unknown execution is never settled")

let private settlementUnconfirmed failure throws label =
    testCase label (fun () ->
        let recovery =
            new CoreRecoveryStore.Store(settleFailure = failure, settleThrows = throws)

        let core, claims = makeCore Normal recovery
        let operationId = Guid.NewGuid()

        let digest =
            prepared core operationId ("REC-SETTLEMENT-" + operationId.ToString("N"))

        match resolve core operationId digest CancellationToken.None with
        | ResolveOutcome.ResolveCompleted(_,
                                          _,
                                          DefiniteExecution.Accepted receipt,
                                          SettlementConfirmation.Unconfirmed) ->
            Expect.equal receipt.OperationId operationId "Definite accepted receipt remains"
        | _ -> failtest "Settlement loss must not erase definite acceptance."

        Expect.equal claims.TransactionCalls 1 "One definite claim transaction"
        Expect.equal recovery.SettlementCalls 1 "Settlement was attempted once")

let private failedBeforeCommitSettlement =
    testCase "[CC-REC-001] definite pre-commit failure is settled as a failure" (fun () ->
        let recovery = new CoreRecoveryStore.Store()
        let core, _ = makeCore FailsBeforeCommit recovery
        let operationId = Guid.NewGuid()
        let digest = prepared core operationId "REC-FAILED-BEFORE-COMMIT"

        match resolve core operationId digest CancellationToken.None with
        | ResolveOutcome.ResolveCompleted(_,
                                          _,
                                          DefiniteExecution.FailedBeforeCommit(actual, fault),
                                          SettlementConfirmation.Confirmed) ->
            Expect.equal actual operationId "Failed operation identity"
            Expect.equal fault.Code FaultCode.StoreUnavailable "Definite pre-commit failure"
        | _ -> failtest "A definite pre-commit failure must retain its own outcome."

        Expect.equal recovery.SettlementCalls 1 "Definite failure was settled")

let private rejectedSettlement =
    testCase "[CC-REC-001] definite stale rejection is settled without another receipt" (fun () ->
        let claims = new CoreStore.Store()
        let recovery = new CoreRecoveryStore.Store()
        let claimPort = claims :> IClaimStore
        let core = CoreApi.create claimPort (recovery :> IRecoveryStore) clock
        let operationId = Guid.NewGuid()
        let reference = "REC-STALE-" + operationId.ToString("N")
        let digest = prepared core operationId reference

        let competing =
            draft (Guid.NewGuid()) reference
            |> Drafts.bind
            |> Result.defaultWith (fun _ -> failtest "Synthetic competing command must bind.")

        match Service.executeAsync claimPort clock competing |> fun task -> task.Result with
        | Ok _ -> ()
        | Error _ -> failtest "Competing synthetic open must commit first."

        match resolve core operationId digest CancellationToken.None with
        | ResolveOutcome.ResolveCompleted(_,
                                          _,
                                          DefiniteExecution.ExecutionRejected(actual, rejection),
                                          SettlementConfirmation.Confirmed) ->
            Expect.equal actual operationId "Rejected operation identity"
            Expect.equal rejection.Code RejectionCode.VersionConflict "Authoritative revision"
        | _ -> failtest "Stale preparation must yield a definite settled rejection."

        Expect.equal recovery.SettlementCalls 1 "Definite rejection was settled")

let private receiptBetweenObservationAndAttempt =
    testCase "[CC-REC-001] receipt between observation and attempt returns exact replay" (fun () ->
        let claims = new CoreStore.Store()
        let claimPort = claims :> IClaimStore
        let operationId = Guid.NewGuid()
        let command = draft operationId ("REC-INTERLEAVE-" + operationId.ToString("N"))

        let request =
            Drafts.bind command
            |> Result.defaultWith (fun _ -> failtest "Synthetic interleaved command must bind.")

        let commitBetween () =
            match Service.executeAsync claimPort clock request |> fun task -> task.Result with
            | Ok _ -> ()
            | Error _ -> failtest "Synthetic receipt must commit at the interleave barrier."

        let recovery = new CoreRecoveryStore.Store(onStart = commitBetween)
        let core = CoreApi.create claimPort (recovery :> IRecoveryStore) clock
        let digest = prepared core operationId command.CaseReference

        match resolve core operationId digest CancellationToken.None with
        | ResolveOutcome.ResolveCompleted(_,
                                          _,
                                          DefiniteExecution.Accepted receipt,
                                          SettlementConfirmation.Confirmed) ->
            Expect.equal receipt.OperationId operationId "Exact operation receipt"
            Expect.isTrue receipt.Replayed "Core transaction observes intervening acceptance"
            Expect.equal receipt.Snapshot.Version 1L "No second revision"
        | _ -> failtest "Intervening exact receipt must replay without a second revision."

        Expect.equal claims.TransactionCalls 2 "One commit and one exact replay transaction"
        Expect.equal recovery.SettlementCalls 1 "Definite replay attempt settled")

let tests =
    testList
        "recovery uncertainty boundaries"
        [
            beforeAttemptCancellation
            afterAttemptCancellation
            admissionUnknown
            unresolvedExecution
                Throws
                "[CC-REC-001] thrown execution after admission remains unresolved"
            unresolvedExecution Unknown "[CC-REC-001] commit-unknown execution is never settled"
            settlementUnconfirmed
                RecoveryStoreFailure.TechnicalMutationUnknown
                false
                "[CC-REC-001] settlement error preserves definite acceptance"
            settlementUnconfirmed
                RecoveryStoreFailure.TechnicalMutationUnknown
                true
                "[CC-REC-001] thrown settlement preserves definite acceptance"
            failedBeforeCommitSettlement
            rejectedSettlement
            receiptBetweenObservationAndAttempt
        ]
