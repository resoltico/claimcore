module ClaimCore.IntegrationTests.RecoveryDisclosureTests

open System
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.RecoveryAuthorityInterleaveFixture
open ClaimCore.IntegrationTests.MutationDisclosureFixture

let private changeUnrelated (fixture: Fixture) started =
    task {
        match started with
        | Ok(RecoveryStart.Started _) -> ()
        | _ -> failtest "The controlled invocation requires a fresh real admission."

        let! changed =
            fixture.OwnerCore.Management.SetEnabled(Guid.NewGuid(), fixture.Outsider, false, ct)

        appliedActor changed |> ignore
        return started
    }

let private setRecoveryGrant (fixture: Fixture) active =
    fixture.OwnerCore.Management.SetGrant(
        Guid.NewGuid(),
        fixture.Owner,
        Role.RecoveryOperator,
        GrantTarget.Installation,
        active,
        ct
    )
    |> await
    |> appliedActor
    |> ignore

let private unresolvedWork (fixture: Fixture) recordAttempt () =
    task {
        let! result =
            (fixture.Controlled(changeUnrelated fixture))
                .Recovery.Resolve(fixture.Input.OperationId, fixture.Digest, ct)

        match result with
        | ResolveOutcome.ResolveAttemptUnresolved(summary,
                                                  attemptId,
                                                  CoreFault.RecoveryAccessUnavailable) ->
            recordAttempt attemptId

            Expect.equal
                summary.OperationId
                fixture.Input.OperationId
                "Exact operation survived current invocation refusal"

            Expect.equal
                summary.RequestSha256
                (Some fixture.Digest)
                "Exact request survived current invocation refusal"
        | _ -> failtest "An admitted authority overlap must retain unresolved attempt knowledge."

        return result
    }

let private withheldUnresolved () =
    withPreparation (fun fixture ->
        let admitted = ref Guid.Empty

        use admission =
            fencedAdmission fixture.ApplicationConnection fixture.Witness (fun () ->
                setRecoveryGrant fixture false)

        requireWithheld (fun () ->
            admission.RunDisclosing(
                unresolvedWork fixture (fun attempt -> admitted.Value <- attempt),
                ActorMutationDisclosure.resolve
                    fixture.Gate
                    fixture.Owner
                    fixture.Input.OperationId
            )
            |> await
            |> ignore)

        Expect.equal
            (fixture.CountAccepted())
            0L
            "Withheld unresolved work has no accepted business effect"

        setRecoveryGrant fixture true

        match
            fixture.OwnerCore.Recovery.Inspect(fixture.Input.OperationId, None, 10, ct)
            |> await
        with
        | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found(RecoveryInspection.RetainedInspection detail)) ->
            Expect.equal
                detail.Preparation.Summary.OperationId
                fixture.Input.OperationId
                "Inspection preserves the original operation"

            Expect.equal
                detail.Preparation.Summary.RequestSha256
                (Some fixture.Digest)
                "Inspection preserves the original request"

            Expect.equal
                detail.Preparation.Attempts.Items.Head.AttemptId
                admitted.Value
                "The actual admitted identity survives withholding"

            Expect.equal
                detail.Preparation.Attempts.Items.Length
                1
                "The exact admitted attempt remains"

            Expect.isNone
                detail.Preparation.Attempts.Items.Head.Settlement
                "Disclosure refusal does not settle historical uncertainty"
        | _ -> failtest "Restored inspection must return exact retained attempt evidence.")

let tests =
    testCase
        "[CC-AUTH-001][CC-REC-001] unresolved recovery payload is withheld after final disclosure authority loss"
        withheldUnresolved
