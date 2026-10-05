module internal ClaimCore.IntegrationTests.InstallationLossRetirementReadOrder

open System
open System.Data
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.InstallationLossRetirementFixture
open ClaimCore.IntegrationTests.InstallationLossRetirementCrashFixture

let private readWaitingOnPrimary (observer: NpgsqlConnection) =
    use command =
        new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_stat_activity "
            + "WHERE datname=current_database() AND usename='claimcore_app' "
            + "AND wait_event_type='Lock' "
            + "AND query LIKE 'SELECT revision FROM claimcore.authority_tip WHERE singleton FOR SHARE%')",
            observer
        )

    command.ExecuteScalar() :?> bool

/// A primary-first terminal writer must be able to append W0 while an actor read
/// waits for its primary shared barrier, without the read holding a witness lease.
let intentWhileReadWaits (context: Context) (decision: Decision) =
    use primary = new NpgsqlConnection(context.OwnerConnectionString)
    primary.Open()
    use transaction = primary.BeginTransaction(IsolationLevel.ReadCommitted)
    InstallationLossRetirementState.primaryIdentity primary transaction |> ignore
    InstallationLossRetirementState.authorityRevision primary transaction |> ignore

    let reading =
        Task.Run(fun () ->
            (context.Runtime.ForActor(human "loss-owner-first")).Definition(CancellationToken.None)
            |> await
            |> ignore)

    use observer = new NpgsqlConnection(context.OwnerConnectionString)
    observer.Open()

    let waiting = SpinWait.SpinUntil((fun () -> readWaitingOnPrimary observer), 10000)

    let preparing = Task.Run(fun () -> w0 context decision)

    let completed, pending =
        try
            let finished = preparing.Wait(TimeSpan.FromSeconds 10.)

            finished,
            (context.Witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())
                .LossRetirementPending
        finally
            transaction.Rollback()

    Expect.isTrue waiting "Actor read reached the primary shared barrier."
    Expect.isTrue completed "W0 did not wait behind an actor witness read lease."
    Expect.isTrue pending "W0 fenced the installation before primary lock release."

    Expect.throwsT<PostgresException>
        (fun () -> reading.GetAwaiter().GetResult())
        "Read cannot disclose after the terminal W0 fence."

    preparing.GetAwaiter().GetResult()
