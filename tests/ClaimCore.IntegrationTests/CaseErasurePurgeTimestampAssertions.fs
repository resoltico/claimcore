module internal ClaimCore.IntegrationTests.CaseErasurePurgeTimestampAssertions

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures

let assertUnalignedPurgeRefused
    owner
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    commitments
    (inventory: IManagedCopyErasureClearance)
    caseId
    (change: LifecycleChange)
    (cancellation: CancellationToken)
    =
    let unaligned =
        match change.Action with
        | LifecycleMutation.PurgeLivePayload(reason, validUntil) ->
            { change with
                Action = LifecycleMutation.PurgeLivePayload(reason, validUntil.AddTicks(1L))
            }
        | _ -> failtest "Synthetic purge action is absent."

    let before =
        (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence

    Expect.equal
        (CaseErasurePurge.execute
            owner
            connection
            witness
            commitments
            inventory
            (CaseLifecycleCandidate.draft caseId unaligned)
            cancellation
         |> await)
        (OwnerPurgeOutcome.Refused OwnerPurgeRefusal.ProposalMismatch)
        "Unroundtrippable purge deadline is refused before authority intent."

    Expect.equal
        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        before
        "Invalid purge deadline does not advance witness authority."
