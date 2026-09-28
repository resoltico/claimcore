module ClaimCore.IntegrationTests.CaseErasurePurgeAdmissionTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests
open ClaimCore.IntegrationTests.CaseErasurePurgeTests

let private ct = CancellationToken.None

let private noInventory: IManagedCopyErasureClearance =
    { new IManagedCopyErasureClearance with
        member _.RequireCompleteInventory(_, _, _, _, _, _, _) =
            Task.FromResult<ManagedCopyInventorySeal option> None
    }

let private pending action =
    CaseLifecycleStoreFixture.setup (fun owner _ witness runtime proposer first second _ _ ->
        let actor, input, change = proposal runtime proposer first second
        let id = caseId owner input.CaseReference
        let draft = CaseLifecycleCandidate.draft id change
        let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity
        use connection = new NpgsqlConnection(owner)
        connection.Open()
        action owner witness actor input change id draft commitments connection)

let private liveCaseRemains connection id =
    use command =
        new NpgsqlCommand("SELECT count(*) FROM claimcore.cases WHERE case_id=@case", connection)

    Sql.uuid command "case" id
    Expect.equal (command.ExecuteScalar() :?> int64) 1L "Live case was not deleted"

let private inventoryUnknown =
    testCase
        "[CC-ERASE-001] missing complete copy inventory refuses live purge before intent"
        (fun _ ->
            pending (fun owner witness _ _ _ id draft commitments connection ->
                let before = witness.Snapshot().TipSequence

                match
                    CaseErasurePurge.execute
                        owner
                        connection
                        witness
                        commitments
                        noInventory
                        draft
                        ct
                    |> await
                with
                | OwnerPurgeOutcome.InventoryUnknown -> ()
                | _ -> failtest "Unknown inventory was not refused."

                Expect.equal
                    (witness.Snapshot().TipSequence)
                    before
                    "No purge intent was appended"

                liveCaseRemains connection id))

let private holdAfterApprovals =
    testCase "[CC-ERASE-001] witnessed hold after approvals still fences owner purge" (fun _ ->
        pending (fun owner witness actor input _ id draft commitments connection ->
            let current = review actor input.CaseReference

            let hold =
                change
                    (Guid.NewGuid())
                    input.CaseReference
                    current
                    (LifecycleMutation.RecordHold(
                        Guid.NewGuid(),
                        "Synthetic retention hold",
                        DateOnly.FromDateTime(DateTime.UtcNow.AddDays 30.0)
                    ))

            match actor.Lifecycle.Apply(hold, ct) |> await with
            | LifecycleWriteOutcome.Applied _ -> ()
            | _ -> failtest "Synthetic hold did not settle."

            let before = witness.Snapshot().TipSequence

            match
                CaseErasurePurge.execute
                    owner
                    connection
                    witness
                    commitments
                    (syntheticInventory id)
                    draft
                    ct
                |> await
            with
            | OwnerPurgeOutcome.Refused OwnerPurgeRefusal.HoldActive -> ()
            | _ -> failtest "Active hold was not retained."

            Expect.equal (witness.Snapshot().TipSequence) before "Hold refusal appended no purge"
            liveCaseRemains connection id))

let private orphanIntent =
    testCase "[CC-ERASE-001] orphan CASE intent keeps owner purge unknowable" (fun _ ->
        pending (fun owner witness _ _ _ id draft commitments connection ->
            witness.BeginAuthority(Guid.NewGuid(), [| 0x43uy; 0x43uy; 0x55uy |], Some id)
            |> ignore

            let before = witness.Snapshot().TipSequence

            match
                CaseErasurePurge.execute
                    owner
                    connection
                    witness
                    commitments
                    (syntheticInventory id)
                    draft
                    ct
                |> await
            with
            | OwnerPurgeOutcome.IdentityCoverageUnknowable -> ()
            | _ -> failtest "Unknown case intent was not preserved."

            Expect.equal (witness.Snapshot().TipSequence) before "No purge intent followed orphan"
            liveCaseRemains connection id))

let tests =
    testList "case erasure owner admission" [ inventoryUnknown; holdAfterApprovals; orphanIntent ]
