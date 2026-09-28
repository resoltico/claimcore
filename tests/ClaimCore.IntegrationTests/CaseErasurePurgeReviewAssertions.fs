module internal ClaimCore.IntegrationTests.CaseErasurePurgeReviewAssertions

open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures

let assertOpaqueTombstone
    (source: NpgsqlDataSource)
    (runtime: Runtime)
    first
    id
    (change: LifecycleChange)
    (commitments: ISuppressionCommitments)
    (cancellation: CancellationToken)
    =
    let gate = new PostgresActorGate(source, commitments) :> IActorGate

    let context =
        gate.Tombstone(first, EndpointAction.ReviewTombstone, id, cancellation) |> await

    Expect.isSome context "Current steward must be admitted to the opaque tombstone"

    match (runtime.ForActor first).Tombstones.Review(id, cancellation) |> await with
    | TombstoneReviewOutcome.Available review when
        review.CaseId = id
        && review.PurgeEventId = change.EventId
        && review.TargetCount > 0L
        && review.RequiredDistinctStewardApprovals = 2
        ->
        ()
    | _ -> failtest "Steward tombstone seal review was unavailable."
