module ClaimCore.IntegrationTests.CaseListBlockedTimingTests

open System
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests
open ClaimCore.IntegrationTests.ActorBoundCoreTestSupport

let private requireErasureRequest (actor: IActorClaimsCore) request =
    let current = review actor request.CaseReference

    let proposal =
        change
            (Guid.NewGuid())
            request.CaseReference
            current
            (LifecycleMutation.RequestErasure "Synthetic privacy request")

    match actor.Lifecycle.Apply(proposal, CancellationToken.None) |> await with
    | LifecycleWriteOutcome.Applied _ -> ()
    | _ -> failtest "Synthetic erasure request must settle."

let private blockedIdentitiesShareClass () =
    setup (fun owner _ _ runtime proposer _ _ ungranted _ ->
        let actor = runtime.ForActor proposer
        let denied = runtime.ForActor ungranted
        let voided, _ = voidCase owner actor denied
        requireComparableDenialTiming denied voided.CaseReference voided.OperationId

        let erased =
            openRequest (Guid.NewGuid()) ("ERASURE-TIMING-" + Guid.NewGuid().ToString("N"))

        executeAccepted actor erased
        requireErasureRequest actor erased
        requireComparableDenialTiming denied erased.CaseReference erased.OperationId)

let tests =
    testCase
        "[CC-AUTH-001] voided and erasure-fenced identities share denial timing class"
        blockedIdentitiesShareClass
