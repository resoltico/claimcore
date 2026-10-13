module ClaimCore.IntegrationTests.CaseLifecycleProofBatchTests

open System
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.ReadProofMeasurements

let private heldClaim (actor: IActorClaimsCore) =
    let reference = "held-proof-" + Guid.NewGuid().ToString("N")

    match
        actor.Execute(openRequest (Guid.NewGuid()) reference, CancellationToken.None)
        |> await
    with
    | SubmissionOutcome.Completed(_,
                                  _,
                                  DefiniteExecution.Accepted _,
                                  SettlementConfirmation.Confirmed) -> ()
    | _ -> failtest "Synthetic opening must be definite."

    let review = CaseLifecycleStoreTests.review actor reference

    let request =
        CaseLifecycleStoreTests.change
            (Guid.NewGuid())
            reference
            review
            (LifecycleMutation.RecordHold(
                Guid.NewGuid(),
                "Synthetic evidence hold",
                DateOnly(2027, 1, 1)
            ))

    match actor.Lifecycle.Apply(request, CancellationToken.None) |> await with
    | LifecycleWriteOutcome.Applied _ -> ()
    | _ -> failtest "Synthetic hold must be independently settled."

    match actor.Get(reference, CancellationToken.None) |> await with
    | QueryOutcome.Succeeded(Lookup.Found current) -> Claim.restore current.Record |> accepted
    | _ -> failtest "An active held case remains readable."

let private proof count =
    testCase
        ($"[CC-AUTH-001] current proof batches {count} cases with lifecycle evidence")
        (fun () ->
            CaseLifecycleStoreFixture.setup (fun _ (_, app) witness runtime principal _ _ _ _ ->
                let actor = runtime.ForActor principal
                let claims = [ for _ in 1..count -> heldClaim actor ]

                withFence app witness (fun connection transaction ->
                    task {
                        let! failure, commands =
                            measure "lifecycle" count (fun () ->
                                CaseReadEvidence.page
                                    witness
                                    connection
                                    transaction
                                    claims
                                    CancellationToken.None)

                        Expect.isNone
                            failure
                            "Each case proves both accepted fields and current lifecycle."

                        Expect.isLessThanOrEqual
                            commands
                            (((count + 3) / 4) * 14)
                            "At most eight acceptance/lifecycle units per current-case chunk."
                    })
                |> await))

let tests =
    [ 1; 8; 9; 50 ] |> List.map proof |> testList "current lifecycle proof units"
