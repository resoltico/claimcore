module ClaimCore.IntegrationTests.CaseLifecycleCapacityTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests

let private cancellation = CancellationToken.None

let private openedCase (actor: IActorClaimsCore) =
    let input = openRequest (Guid.NewGuid()) ("LIFE-C-" + Guid.NewGuid().ToString("N"))
    executeAccepted actor input
    input

let private concurrentApprovalSlots =
    testCase "[CC-LIFE-001] concurrent stewards cannot consume a third approval slot" (fun _ ->
        setup (fun _ _ _ runtime proposer first second _ _ ->
            let actor = runtime.ForActor proposer
            let input = openedCase actor
            let current = review actor input.CaseReference

            let proposed =
                change
                    (Guid.NewGuid())
                    input.CaseReference
                    current
                    (LifecycleMutation.VoidDataEntryError "Synthetic concurrent approval")

            let expiry = DateTimeOffset.UtcNow.AddHours 1.0

            let attempts =
                [ proposer; first; second ]
                |> List.map (fun principal ->
                    (runtime.ForActor principal)
                        .Lifecycle.Approve(proposed, Guid.NewGuid(), expiry, cancellation))
                |> List.toArray

            let outcomes = Task.WhenAll(attempts) |> await |> Array.toList

            let applied =
                outcomes
                |> List.filter (function
                    | LifecycleWriteOutcome.Applied _ -> true
                    | _ -> false)

            let full =
                outcomes
                |> List.filter (function
                    | LifecycleWriteOutcome.Refused LifecycleRefusal.ApprovalCapacityExceeded ->
                        true
                    | _ -> false)

            Expect.equal applied.Length 2 "Exactly two independent approvals commit"
            Expect.equal full.Length 1 "Third approval is refused before witness intent"))

let private seedHolds owner source principal reference =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use lookup =
        new NpgsqlCommand(
            "SELECT case_id FROM claimcore.cases WHERE case_reference=@reference",
            connection
        )

    Sql.text lookup "reference" reference
    let caseId = lookup.ExecuteScalar() :?> Guid
    let actor = actorId (new ActorGrantStore(source)) principal

    use insert =
        new NpgsqlCommand(
            "INSERT INTO claimcore.case_holds "
            + "(hold_id,case_id,ground,review_on,recorded_by,recorded_at) "
            + "SELECT gen_random_uuid(),@caseId,'Synthetic retention ground',"
            + "DATE '2026-10-01',@actor,TIMESTAMPTZ '2026-09-24 12:00:00+00' "
            + "FROM generate_series(1,256)",
            connection
        )

    Sql.uuid insert "caseId" caseId
    Sql.uuid insert "actor" actor
    Expect.equal (insert.ExecuteNonQuery()) 256 "Exactly 256 synthetic active holds seeded"

let private holdCapacityUnderLock =
    testCase "[CC-LIFE-001] 257th active hold is refused before a witness intent" (fun _ ->
        setup (fun owner (source, _) witness runtime proposer _ _ _ _ ->
            let actor = runtime.ForActor proposer
            let input = openedCase actor
            seedHolds owner source proposer input.CaseReference
            let current = review actor input.CaseReference
            Expect.equal current.ActiveHolds.Length 256 "Bounded review exposes all active holds"
            let before = witness.Snapshot().TipSequence

            let proposed =
                change
                    (Guid.NewGuid())
                    input.CaseReference
                    current
                    (LifecycleMutation.RecordHold(
                        Guid.NewGuid(),
                        "Synthetic excess hold",
                        DateOnly(2026, 10, 1)
                    ))

            match actor.Lifecycle.Apply(proposed, cancellation) |> await with
            | LifecycleWriteOutcome.Refused LifecycleRefusal.HoldCapacityExceeded -> ()
            | _ -> failtest "Excess hold was not refused."

            Expect.equal
                (witness.Snapshot().TipSequence)
                before
                "A refused hold cannot append authority evidence"))

let tests =
    testList "case lifecycle capacity" [ concurrentApprovalSlots; holdCapacityUnderLock ]
