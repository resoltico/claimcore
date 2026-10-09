module ClaimCore.Tests.RecoveryAdmissionOutcomeTests

open System
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Tests.Fixtures

let private corruptCanonical (preparation: RetainedPreparation) =
    { preparation with
        CanonicalRequest = [| 255uy |]
    }

let private corruptDomain (preparation: RetainedPreparation) =
    match
        RequestRecord.decode SemanticContract.current.RequestByteLimit preparation.CanonicalRequest
    with
    | Ok({ Command = Command.Open registration } as request) ->
        { preparation with
            CanonicalRequest =
                RequestRecord.encode
                    { request with
                        Command =
                            Command.Open
                                { registration with
                                    IncidentDate = "2026-02-30"
                                }
                    }
        }
    | _ -> failtest "Synthetic opening must have valid canonical syntax."

let private retained (recovery: IRecoveryStore) operationId =
    match recovery.Get(operationId, CancellationToken.None).Result with
    | Ok(Some(RecoveryStoredOperation.Retained(preparation, _))) -> preparation
    | _ -> failtest "Original synthetic material must remain available."

let private inspectedAttempt (core: IClaimsCore) operationId =
    match core.Recovery.Inspect(operationId, None, 10, CancellationToken.None).Result with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found(RecoveryInspection.RetainedInspection detail)) ->
        Expect.equal detail.Preparation.Attempts.Items.Length 1 "One actual admitted attempt"
        Expect.equal detail.Observation (Lookup.NotFound operationId) "No accepted effect"
        let attempt = List.head detail.Preparation.Attempts.Items
        Expect.isNone attempt.Settlement "No invented historical settlement"
        attempt.AttemptId
    | _ -> failtest "Known admission must remain independently inspectable."

let private requireRetainedFault core (input: CommandRequest) digest prior expected result =
    match result with
    | ResolveOutcome.ResolveAttemptUnresolved(summary, attemptId, fault) ->
        Expect.equal summary.OperationId input.OperationId "Original operation identity"
        Expect.equal summary.RequestSha256 (Some digest) "Original exact digest"
        Expect.equal summary.CaseReference input.CaseReference "Previously validated case identity"

        Expect.equal
            summary.State
            PreparationState.SubmissionStarted
            "Known admission is not erased"

        Expect.equal fault expected "Specific retained failure remains separate from knowledge"

        Expect.equal
            attemptId
            (inspectedAttempt core input.OperationId)
            "Exact actual attempt identity"

        prior
        |> Option.iter (fun id -> Expect.equal attemptId id "Historical attempt is unchanged")
    | _ -> failtest "Validation after admission must preserve unresolved attempt knowledge."

let private retainedFault historical invalidDomain =
    let change = if invalidDomain then corruptDomain else corruptCanonical
    let recovery = new CoreRecoveryStore.Store(transformStarted = change)
    let claims = new CoreStore.Store()
    recovery.AttachClaimStore(claims :> IClaimStore)

    let core =
        ActorCoreFixture.create
            (claims :> IClaimStore)
            (recovery :> IRecoveryStore)
            (businessTime today)

    let input = request 0L (Command.Open registration)

    let digest =
        match core.Prepare(input, CancellationToken.None).Result with
        | PrepareOutcome.Prepared(details, _) -> details.Summary.RequestSha256.Value
        | _ -> failtest "Valid original preparation must be retained."

    let originalBytes =
        Array.copy (retained (recovery :> IRecoveryStore) input.OperationId).CanonicalRequest

    let prior =
        if historical then
            match
                (recovery :> IRecoveryStore).Start(input.OperationId, CancellationToken.None).Result
            with
            | Ok(RecoveryStart.Started(id, _)) -> Some id
            | _ -> failtest "Historical start must be confirmed."
        else
            None

    let expected =
        if invalidDomain then
            CoreFault.RetainedDomainShapeInvalid
        else
            CoreFault.RetainedCanonicalInvalid

    core.Recovery.Resolve(input.OperationId, digest, CancellationToken.None).Result
    |> requireRetainedFault core input digest prior expected

    Expect.equal recovery.StartCalls (if historical then 2 else 1) "No implicit admission retries"
    Expect.equal claims.TransactionCalls 0 "Corrupt returned material is never executed"
    Expect.equal recovery.SettlementCalls 0 "No invented definite settlement"

    Expect.sequenceEqual
        (retained (recovery :> IRecoveryStore) input.OperationId).CanonicalRequest
        originalBytes
        "Original stored request bytes remain exact"

let private corruptBeforeAdmission () =
    let recovery = new CoreRecoveryStore.Store(transformGet = corruptCanonical)
    let claims = new CoreStore.Store()
    recovery.AttachClaimStore(claims :> IClaimStore)

    let core =
        ActorCoreFixture.create
            (claims :> IClaimStore)
            (recovery :> IRecoveryStore)
            (businessTime today)

    let input = request 0L (Command.Open registration)

    let digest =
        match core.Prepare(input, CancellationToken.None).Result with
        | PrepareOutcome.Prepared(details, _) -> details.Summary.RequestSha256.Value
        | _ -> failtest "Valid original must be prepared."

    match core.Recovery.Resolve(input.OperationId, digest, CancellationToken.None).Result with
    | ResolveOutcome.ResolveFailedBeforeAttempt(None, CoreFault.RetainedCanonicalInvalid) -> ()
    | _ -> failtest "A pre-admission validation failure must remain before attempt."

    Expect.equal recovery.StartCalls 0 "No attempt admission"
    Expect.equal claims.TransactionCalls 0 "No claim execution"
    Expect.equal recovery.SettlementCalls 0 "No settlement"


let private requireExactRecovery (core: IClaimsCore) operationId digest attemptId =
    match core.Recovery.Resolve(operationId, digest, CancellationToken.None).Result with
    | ResolveOutcome.ResolveCompleted(_,
                                      actual,
                                      DefiniteExecution.Accepted receipt,
                                      SettlementConfirmation.Confirmed) ->
        Expect.equal actual attemptId "Exact admitted attempt is recovered"
        Expect.equal receipt.OperationId operationId "Exact original operation is accepted"
        Expect.equal receipt.Snapshot.Version 1L "Recovery commits only one revision"
    | _ -> failtest "Later exact recovery must finish the same admitted attempt."

let private faultedStart cancelAfterEffect () =
    use cancellation = new CancellationTokenSource()
    let mutable first = true

    let loseReply () =
        if first then
            first <- false

            if cancelAfterEffect then
                cancellation.Cancel()
                raise (OperationCanceledException(cancellation.Token))
            else
                raise (System.IO.IOException("synthetic lost admission reply"))

    let recovery = new CoreRecoveryStore.Store(onStart = loseReply)
    let claims = new CoreStore.Store()
    recovery.AttachClaimStore(claims :> IClaimStore)

    let core =
        ActorCoreFixture.create
            (claims :> IClaimStore)
            (recovery :> IRecoveryStore)
            (businessTime today)

    let input = request 0L (Command.Open registration)

    let digest =
        match core.Prepare(input, CancellationToken.None).Result with
        | PrepareOutcome.Prepared(details, _) -> details.Summary.RequestSha256.Value
        | _ -> failtest "Original valid request must be retained."

    let originalBytes =
        Array.copy (retained (recovery :> IRecoveryStore) input.OperationId).CanonicalRequest

    match core.Recovery.Resolve(input.OperationId, digest, cancellation.Token).Result with
    | ResolveOutcome.ResolveAttemptAdmissionUnknown(summary, CoreFault.RecoveryMutationUnknown) ->
        Expect.equal summary.OperationId input.OperationId "Original operation identity"
        Expect.equal summary.RequestSha256 (Some digest) "Original exact digest"
    | _ -> failtest "An invoked Start with lost delivery cannot prove non-admission."

    let attemptId = inspectedAttempt core input.OperationId
    Expect.equal recovery.StartCalls 1 "One admission invocation"
    Expect.equal claims.TransactionCalls 0 "No execution after lost admission delivery"
    Expect.equal recovery.SettlementCalls 0 "No fabricated settlement"

    Expect.sequenceEqual
        (retained (recovery :> IRecoveryStore) input.OperationId).CanonicalRequest
        originalBytes
        "Stored request bytes are unchanged"

    requireExactRecovery core input.OperationId digest attemptId
    Expect.equal recovery.SettlementCalls 1 "Only later exact execution settles that attempt"

let tests =
    testList
        "admitted recovery knowledge"
        [
            testCase
                "[CC-REC-001] corrupt canonical reply after fresh start preserves exact attempt"
                (fun () -> retainedFault false false)
            testCase
                "[CC-REC-001] corrupt canonical reply after historical start preserves exact attempt"
                (fun () -> retainedFault true false)
            testCase
                "[CC-REC-001] invalid Domain reply after fresh start preserves exact attempt"
                (fun () -> retainedFault false true)
            testCase
                "[CC-REC-001] invalid Domain reply after historical start preserves exact attempt"
                (fun () -> retainedFault true true)
            testCase
                "[CC-REC-001] corrupt canonical read before admission starts no attempt"
                corruptBeforeAdmission
            testCase
                "[CC-REC-001] faulted Start delivery preserves admission uncertainty and exact recovery"
                (faultedStart false)
            testCase
                "[CC-REC-001] cancellation exception after Start preserves admission uncertainty and exact recovery"
                (faultedStart true)
        ]
