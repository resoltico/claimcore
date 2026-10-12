module ClaimCore.IntegrationTests.ActivationReviewCancellationTests

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.RealDataActivationPlanFixture

let private blockedReview owner blocker =
    use monitor = new NpgsqlConnection(owner)
    monitor.Open()

    use query =
        new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE @blocker=ANY(pg_blocking_pids(pid)) "
            + "AND wait_event_type='Lock' AND query LIKE '%FROM claimcore.authority_tip%')",
            monitor
        )

    query.Parameters.AddWithValue("blocker", blocker) |> ignore

    Expect.isTrue
        (SpinWait.SpinUntil((fun () -> query.ExecuteScalar() :?> bool), 5000))
        "The native review reached its actual PostgreSQL authority wait."

let private cancellation owner app writer witness profile =
    let principal = human "cancelled-activation-review-owner"
    provision owner witness principal |> applied
    let planId, _, _ = publishSyntheticPlan owner witness profile
    use runtime = openRuntime app writer
    let core = runtime.ForActor principal
    use connection = new NpgsqlConnection(owner)
    connection.Open()
    use transaction = connection.BeginTransaction()

    use barrier =
        new NpgsqlCommand(
            "SELECT revision FROM claimcore.authority_tip WHERE singleton FOR UPDATE",
            connection,
            transaction
        )

    barrier.ExecuteScalar() |> ignore
    use caller = new CancellationTokenSource()
    let work = core.ReviewRealDataActivation(planId, caller.Token)

    try
        blockedReview owner connection.ProcessID
        Expect.isFalse work.IsCompleted "The held lock keeps the review in flight."
        caller.Cancel()

        Expect.isTrue
            (SpinWait.SpinUntil((fun () -> work.IsCompleted), 2000))
            "Caller cancellation ends the read while the authority lock remains held."

        match work |> await with
        | RealDataActivationPlanReviewOutcome.ResourceUnavailable -> ()
        | _ -> failtest "Cancellation cannot disclose a plan or fabricate a mutation result."
    finally
        transaction.Rollback()
        work |> await |> ignore

    match core.ReviewRealDataActivation(planId, CancellationToken.None) |> await with
    | RealDataActivationPlanReviewOutcome.Reviewed review ->
        Expect.equal review.PlanId planId "A fresh review proves the connection and fence drained."
    | _ -> failtest "The exact synthetic plan remains readable after cancellation."

let tests =
    testCase
        "[CC-BACKUP-001][CC-WEB-001] activation review cancellation ends an actual PostgreSQL wait"
        (fun _ -> withRealDataBootstrap cancellation)
