module ClaimCore.Tests.RecoveryAccessOutcomeTests

open System
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private denied afterAdmission =
    let recovery =
        if afterAdmission then
            new CoreRecoveryStore.Store(settleFailure = RecoveryStoreFailure.ResourceUnavailable)
        else
            new CoreRecoveryStore.Store(startFailure = RecoveryStoreFailure.ResourceUnavailable)

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
        | PrepareOutcome.Prepared(details, _) ->
            details.Summary.RequestSha256
            |> Option.defaultWith (fun () -> failtest "Exact synthetic digest is required.")
        | _ -> failtest "Synthetic preparation must be retained."

    let result =
        core.Recovery.Resolve(input.OperationId, digest, CancellationToken.None).Result

    let summary, fault =
        match afterAdmission, result with
        | false, ResolveOutcome.ResolveFailedBeforeAttempt(Some summary, fault) -> summary, fault
        | true, ResolveOutcome.ResolveAttemptUnresolved(summary, attemptId, fault) ->
            Expect.notEqual attemptId Guid.Empty "Admitted attempt identity survives refusal"
            summary, fault
        | _ -> failtest "Access refusal must preserve the known invocation phase."

    Expect.equal summary.OperationId input.OperationId "Exact operation identity"
    Expect.equal summary.RequestSha256 (Some digest) "Exact request digest"
    Expect.equal fault CoreFault.RecoveryAccessUnavailable "Access-specific cause"
    Expect.equal fault.Code FaultCode.ResourceUnavailable "No malformed-storage classification"
    Expect.equal fault.Action RecommendedAction.RecoverExact "Refusal cannot settle earlier work"
    Expect.equal recovery.SettlementCalls 0 "No invented definite refusal settlement"

    match core.Recovery.Inspect(input.OperationId, None, 10, CancellationToken.None).Result with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found(RecoveryInspection.RetainedInspection detail)) ->
        Expect.equal
            detail.Preparation.Attempts.Items.Length
            (if afterAdmission then 1 else 0)
            "Actual admission evidence"

        Expect.isTrue
            (detail.Preparation.Attempts.Items
             |> List.forall (fun attempt -> attempt.Settlement.IsNone))
            "Historical uncertainty stays unresolved"

        Expect.equal
            detail.Observation
            (Lookup.NotFound input.OperationId)
            "No acceptance is fabricated"
    | _ -> failtest "Retained synthetic evidence must remain inspectable."

let tests =
    testList
        "recovery access phase"
        [
            testCase
                "[CC-REC-001] pre-admission access refusal is not an invalid storage reply"
                (fun () -> denied false)
            testCase
                "[CC-REC-001] admitted access refusal preserves exact unresolved attempt knowledge"
                (fun () -> denied true)
        ]
