module ClaimCore.IntegrationTests.RecoveryAuthorityInterleaveTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport

open ClaimCore.IntegrationTests.RecoveryAuthorityInterleaveFixture

let private inspect (core: IActorClaimsCore) id =
    match core.Recovery.Inspect(id, None, 10, ct) |> await with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found(RecoveryInspection.RetainedInspection detail)) ->
        detail
    | _ -> failtest "Current authorized inspection must return exact retained evidence."

let private interleaveGrant related (historicalAttempt: Guid option) (fixture: Fixture) started =
    let ownerCore, owner, outsider, context =
        fixture.OwnerCore, fixture.Owner, fixture.Outsider, fixture.Captured

    task {
        match started with
        | Ok(RecoveryStart.Started _) when historicalAttempt.IsNone -> ()
        | Ok(RecoveryStart.AlreadyStarted(attemptId, _)) when historicalAttempt = Some attemptId ->
            ()
        | _ ->
            failtest
                "The controlled race requires its exact confirmed fresh or reconciled admission."

        let! changed =
            if related then
                ownerCore.Management.SetGrant(
                    Guid.NewGuid(),
                    owner,
                    Role.RecoveryOperator,
                    GrantTarget.Installation,
                    false,
                    ct
                )
            else
                ownerCore.Management.SetEnabled(Guid.NewGuid(), outsider, false, ct)

        Expect.isGreaterThan
            (appliedActor changed)
            context.Binding.GrantRevision
            "Authority changed after actual admission and before execution"

        return started
    }



let private restoreRelatedGrant
    related
    (ownerCore: IActorClaimsCore)
    owner
    (input: CommandRequest)
    digest
    =
    if related then
        match ownerCore.Recovery.Resolve(input.OperationId, digest, ct) |> await with
        | ResolveOutcome.RefusedBeforeAttempt(None, RecoveryRejection.ResourceUnavailable) -> ()
        | _ -> failtest "Current refusal cannot disclose or settle the earlier admitted attempt."

        ownerCore.Management.SetGrant(
            Guid.NewGuid(),
            owner,
            Role.RecoveryOperator,
            GrantTarget.Installation,
            true,
            ct
        )
        |> await
        |> appliedActor
        |> ignore

let private acceptExact (ownerCore: IActorClaimsCore) (input: CommandRequest) digest =
    match ownerCore.Recovery.Resolve(input.OperationId, digest, ct) |> await with
    | ResolveOutcome.ResolveCompleted(_,
                                      _,
                                      DefiniteExecution.Accepted receipt,
                                      SettlementConfirmation.Confirmed) ->
        Expect.equal
            receipt.OperationId
            input.OperationId
            "Explicit later resolution preserves the operation"

        Expect.equal receipt.Snapshot.Version 2L "Original request accepted once"
    | _ -> failtest "A fresh authorized call may explicitly resolve the unchanged identity."

let private admittedRefusal related historicalAttempt (fixture: Fixture) =
    let input, digest = fixture.Input, fixture.Digest
    let controlled = fixture.Controlled

    match
        (controlled (interleaveGrant related historicalAttempt fixture))
            .Recovery.Resolve(input.OperationId, digest, ct)
        |> await
    with
    | ResolveOutcome.ResolveAttemptUnresolved(summary, attemptId, fault) ->
        Expect.equal
            fault
            CoreFault.RecoveryAccessUnavailable
            "Access is separate from invalid storage"

        Expect.equal summary.OperationId input.OperationId "Operation identity is unchanged"

        Expect.equal summary.RequestSha256 (Some digest) "Retained request digest is unchanged"

        attemptId
    | _ -> failtest "An admitted authority refusal must preserve unresolved knowledge."

let private grantOverlap related reconciled =
    withPreparation (fun fixture ->
        let ownerCore, owner = fixture.OwnerCore, fixture.Owner
        let input, digest = fixture.Input, fixture.Digest

        let historicalAttempt =
            if reconciled then
                Some(fixture.CommitStartWithoutSettlement())
            else
                None

        let attemptId = admittedRefusal related historicalAttempt fixture

        historicalAttempt
        |> Option.iter (fun expected ->
            Expect.equal attemptId expected "Reconciliation reuses the exact historical attempt")

        match ownerCore.Get(input.CaseReference, ct) |> await with
        | QueryOutcome.Succeeded(Lookup.Found current) ->
            Expect.equal
                current.Record.Version
                1L
                "Controlled denied invocation did not add a revision"
        | _ -> failtest "The unrelated case-read grant remains available."

        restoreRelatedGrant related ownerCore owner input digest

        let before = inspect ownerCore input.OperationId

        Expect.equal
            before.Preparation.Attempts.Items.Length
            1
            "Only the original real admission exists"

        Expect.equal
            before.Preparation.Attempts.Items.Head.AttemptId
            attemptId
            "Actual admitted identity is inspectable"

        Expect.isNone
            before.Preparation.Attempts.Items.Head.Settlement
            "Refusal did not invent a settlement"

        acceptExact ownerCore input digest

        let after = inspect ownerCore input.OperationId

        let original =
            after.Preparation.Attempts.Items
            |> List.find (fun item -> item.AttemptId = attemptId)

        Expect.isNone original.Settlement "Later acceptance does not rewrite original uncertainty"

        Expect.equal
            after.Preparation.Attempts.Items.Length
            2
            "Later acceptance has separate attempt evidence")

let private verifyLaterAttempt
    (ownerCore: IActorClaimsCore)
    (input: CommandRequest)
    (original: PreparationAttempt)
    =
    let after = inspect ownerCore input.OperationId

    Expect.equal after.Preparation.Attempts.Items.Length 2 "Later acceptance has its own attempt"

    let earlier =
        after.Preparation.Attempts.Items
        |> List.find (fun item -> item.AttemptId = original.AttemptId)

    Expect.isNone earlier.Settlement "Later acceptance cannot settle the earlier uncertainty"

    Expect.equal
        (after.Preparation.Attempts.Items
         |> List.filter (fun item -> item.Settlement = Some "ACCEPTED")
         |> List.length)
        1
        "Only the new attempt is accepted"

let private lostStartReply =
    testCase
        "[CC-REC-001] lost real start reply and later exact acceptance preserve the earlier unknown attempt"
        (fun () ->
            withPreparation (fun fixture ->
                let ownerCore, input, digest, controlled =
                    fixture.OwnerCore, fixture.Input, fixture.Digest, fixture.Controlled

                let lose value =
                    match value with
                    | Ok(RecoveryStart.Started _) ->
                        Task.FromResult(Error RecoveryStoreFailure.TechnicalMutationUnknown)
                    | _ -> failtest "A fresh real admission is required before losing its reply."

                match
                    (controlled lose).Recovery.Resolve(input.OperationId, digest, ct) |> await
                with
                | ResolveOutcome.ResolveAttemptAdmissionUnknown(summary, _) ->
                    Expect.equal
                        summary.OperationId
                        input.OperationId
                        "The original operation is retained"

                    Expect.equal
                        summary.RequestSha256
                        (Some digest)
                        "The original request bytes are retained"
                | _ -> failtest "Lost admission reply must not be called not-started."

                let before = inspect ownerCore input.OperationId

                Expect.equal
                    before.Preparation.Attempts.Items.Length
                    1
                    "One real admission is persisted"

                let original = before.Preparation.Attempts.Items.Head
                Expect.isNone original.Settlement "The original business attempt remains unknown"

                acceptExact ownerCore input digest

                verifyLaterAttempt ownerCore input original

                match ownerCore.Recovery.Resolve(input.OperationId, digest, ct) |> await with
                | ResolveOutcome.ResolveObservedAccepted receipt ->
                    Expect.equal receipt.Snapshot.Version 2L "Exact replay does not advance again"
                | _ -> failtest "Accepted exact replay must return its receipt."))

let tests =
    testList
        "authority interleave knowledge"
        [
            testCase
                "[CC-AUTH-001] unrelated grant revision change after real admission preserves exact unresolved attempt"
                (fun () -> grantOverlap false false)
            testCase
                "[CC-REC-001] related grant loss before execution preserves old uncertainty through explicit restoration"
                (fun () -> grantOverlap true false)
            testCase
                "[CC-WIT-001][CC-REC-001] reconciled START and later authority change preserve the exact historical unknown attempt"
                (fun () -> grantOverlap false true)
            lostStartReply
        ]
