module internal ClaimCore.IntegrationTests.InstallationLossRetirementCommitFault

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.InstallationLossRetirementFixture
open ClaimCore.IntegrationTests.InstallationLossRetirementCrashFixture

let private scalar (connection: NpgsqlConnection) sql =
    use command = new NpgsqlCommand(sql, connection)
    command.ExecuteScalar()

let private execute (connection: NpgsqlConnection) sql =
    use command = new NpgsqlCommand(sql, connection)
    command.ExecuteNonQuery() |> ignore

let private arm (connection: NpgsqlConnection) lockId =
    execute
        connection
        ("CREATE FUNCTION claimcore.loss_commit_fault() RETURNS trigger LANGUAGE plpgsql "
         + "AS 'BEGIN PERFORM pg_advisory_xact_lock("
         + string lockId
         + "); RETURN NEW; END'")

    execute
        connection
        ("CREATE CONSTRAINT TRIGGER loss_commit_fault AFTER INSERT "
         + "ON claimcore.installation_loss_retirements DEFERRABLE INITIALLY DEFERRED "
         + "FOR EACH ROW EXECUTE FUNCTION claimcore.loss_commit_fault()")

let private disarm ownerConnectionString =
    use cleanup = new NpgsqlConnection(ownerConnectionString)
    cleanup.Open()

    execute
        cleanup
        "DROP TRIGGER IF EXISTS loss_commit_fault ON claimcore.installation_loss_retirements"

    execute cleanup "DROP FUNCTION IF EXISTS claimcore.loss_commit_fault()"

let private waiting (connection: NpgsqlConnection) pid =
    use command =
        new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid=@pid "
            + "AND locktype='advisory' AND NOT granted)",
            connection
        )

    command.Parameters.AddWithValue("pid", pid) |> ignore
    command.ExecuteScalar() :?> bool

let private terminate (connection: NpgsqlConnection) pid =
    use command = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", connection)
    command.Parameters.AddWithValue("pid", pid) |> ignore

    match command.ExecuteScalar() with
    | :? bool as stopped when stopped -> ()
    | _ -> failtest "Synthetic in-COMMIT backend termination was not confirmed."

/// A deferred trigger blocks the actual COMMIT on a lock; terminate that exact
/// test backend while PostgreSQL is inside COMMIT, then remove the synthetic DDL.
let abortDuringCommit (context: Context) (decision: Decision) (intent: Ticket) =
    let lockId = RandomNumberGenerator.GetInt32(1, Int32.MaxValue)
    use controller = new NpgsqlConnection(context.OwnerConnectionString)
    controller.Open()
    arm context.Primary lockId
    use held = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", controller)
    held.Parameters.AddWithValue("key", int64 lockId) |> ignore
    held.ExecuteNonQuery() |> ignore

    try
        let pid = scalar context.Primary "SELECT pg_backend_pid()" :?> int
        use transaction = context.Primary.BeginTransaction()

        InstallationLossRetirementPrimary.insert
            context.Primary
            transaction
            decision.Value
            decision.Canonical
            decision.FirstSignature
            decision.SecondSignature
            intent
            decision.Commitments

        let committing =
            Task.Run<bool>(
                Func<bool>(fun () ->
                    try
                        transaction.Commit()
                        false
                    with _ ->
                        true)
            )

        let mutable blocked = false

        for _ in 1..50 do
            if not blocked then
                blocked <- waiting controller pid

                if not blocked then
                    Thread.Sleep(100)

        if not blocked then
            failtest "Synthetic COMMIT never reached deferred lock."

        terminate controller pid

        if not (committing.Wait(TimeSpan.FromSeconds(10.))) || not committing.Result then
            failtest "Terminated COMMIT was not uncertain to the caller."
    finally
        use unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", controller)
        unlock.Parameters.AddWithValue("key", int64 lockId) |> ignore
        unlock.ExecuteNonQuery() |> ignore
        disarm context.OwnerConnectionString
