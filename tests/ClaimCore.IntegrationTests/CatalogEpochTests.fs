module ClaimCore.IntegrationTests.CatalogEpochTests

open System
open System.Threading.Tasks
open Npgsql
open Expecto
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures

// The guarded admission must still refuse, admit, and never remember a failure. The first group
// drives the mechanism with a counting verification over the real token statement; the second
// drives the runtime's own admission as the runtime role.

let private execute (connection: NpgsqlConnection) sql =
    use command = new NpgsqlCommand(sql, connection)
    command.ExecuteNonQuery() |> ignore

let private openAdmin () =
    let connection = new NpgsqlConnection(adminConnection ())
    connection.Open()
    connection

/// A verification stub that records how often it ran.
let private counting () =
    let runs = ref 0
    runs, (fun () -> runs.Value <- runs.Value + 1)

let private freshScope () = $"probe-{Guid.NewGuid():N}"

let private admit scope connection verify =
    CatalogEpoch.admit scope "claimcore" connection verify

let private unchangedIsVerifiedOnce () =
    use connection = openAdmin ()
    let scope = freshScope ()
    let runs, verify = counting ()

    for _ in 1..3 do
        admit scope connection verify

    Expect.equal runs.Value 1 "Only the first checkout verifies"

let private changeAndReversalAreVerified () =
    use connection = openAdmin ()
    let scope = freshScope ()
    let runs, verify = counting ()
    admit scope connection verify

    use transaction = connection.BeginTransaction()
    execute connection "CREATE TABLE claimcore.probe_table (a integer)"
    admit scope connection verify
    Expect.equal runs.Value 2 "A change verifies again"
    transaction.Rollback()

    admit scope connection verify

    Expect.equal
        runs.Value
        3
        "Only the latest verified state is remembered, so the reversal verifies"

    admit scope connection verify
    Expect.equal runs.Value 3 "The reverted state is then vouched for again"

let private elapsedIntervalIsVerified () =
    use connection = openAdmin ()
    let scope = freshScope ()
    let runs, verify = counting ()

    for _ in 1..3 do
        CatalogEpoch.admitWithin TimeSpan.Zero scope "claimcore" connection verify

    Expect.equal runs.Value 3 "Nothing is trusted for longer than the interval"

let private failureIsNeverRemembered () =
    use connection = openAdmin ()
    let scope = freshScope ()
    let refuse () = raise RuntimeDatabaseMismatch

    for _ in 1..2 do
        Expect.throwsT<RuntimeDatabaseMismatch>
            (fun () -> admit scope connection refuse)
            "A refusal surfaces unchanged"

    let runs, verify = counting ()
    admit scope connection verify
    admit scope connection verify
    Expect.equal runs.Value 1 "Success after refusal verifies once and is then remembered"

let private unreadableTokenIsUnknown () =
    use connection = openAdmin ()
    let scope = freshScope ()
    let runs, verify = counting ()
    admit scope connection verify
    Expect.equal runs.Value 1 "The state is verified and remembered"

    use transaction = connection.BeginTransaction()
    use broken = new NpgsqlCommand("SELECT 1 / 0", connection, transaction)

    Expect.throwsT<PostgresException>
        (fun () -> broken.ExecuteScalar() |> ignore)
        "The transaction is aborted"

    // Every statement now fails, including the token read. The verification, which here issues a
    // statement of its own, must still run and be the one that reports.
    let ran = ref false

    Expect.throwsT<PostgresException>
        (fun () ->
            admit scope connection (fun () ->
                ran.Value <- true
                execute connection "SELECT 1"))
        "The aborted session cannot be vouched for"

    transaction.Rollback()
    Expect.isTrue ran.Value "A token that cannot be read runs the full verification"

let private eachLoginRoleIsVerified () =
    use first = openAdmin ()
    use second = new NpgsqlConnection(appConnection ())
    second.Open()
    let scope = freshScope ()
    let runs, verify = counting ()
    admit scope first verify
    admit scope second verify
    Expect.equal runs.Value 2 "Each login role is verified for itself"

let private concurrentFirstCheckouts () =
    let scope = freshScope ()
    let runs, verify = counting ()

    let checkouts =
        [
            for _ in 1..8 ->
                Task.Run(fun () ->
                    use connection = new NpgsqlConnection(appConnection ())
                    connection.Open()
                    admit scope connection verify)
        ]

    Task.WaitAll(Array.ofList checkouts)
    Expect.isGreaterThan runs.Value 0 "The first checkouts verify"
    Expect.isLessThanOrEqual runs.Value 8 "No checkout verifies more than once"

let private mechanismTests =
    testList
        "catalog change token admission"
        [
            testCase
                "[CC-DB-001] a verified unchanged catalog is not verified again"
                unchangedIsVerifiedOnce
            testCase
                "[CC-DB-001] a changed catalog is verified again and so is its reversal"
                changeAndReversalAreVerified
            testCase "[CC-DB-001] an elapsed interval verifies again" elapsedIntervalIsVerified
            testCase
                "[CC-DB-001] a failed verification is never remembered"
                failureIsNeverRemembered
            testCase
                "[CC-DB-001] an unreadable token means unknown, never unchanged"
                unreadableTokenIsUnknown
            testCase
                "[CC-DB-001] verification follows the connection identity, not a shared entry"
                eachLoginRoleIsVerified
            testCase "[CC-DB-001] concurrent first checkouts all succeed" concurrentFirstCheckouts
        ]

let private asRuntime (connection: NpgsqlConnection) =
    execute connection "SET LOCAL SESSION AUTHORIZATION claimcore_app"

let private asOwner (connection: NpgsqlConnection) =
    execute connection "RESET SESSION AUTHORIZATION"

let private requireRefusal (connection: NpgsqlConnection) message =
    Expect.throwsT<RuntimeDatabaseMismatch>
        (fun () -> RuntimeDatabase.requireCompatible connection)
        message

let private weakenedCheckIsRefusedThenReversed () =
    CatalogEpoch.forget ()
    use connection = openAdmin ()
    use first = connection.BeginTransaction()
    asRuntime connection
    let start = CatalogEpoch.counters ()

    RuntimeDatabase.requireCompatible connection
    let verified = CatalogEpoch.counters ()
    Expect.equal (verified.Verified - start.Verified) 1L "The first checkout verifies in full"

    RuntimeDatabase.requireCompatible connection
    let skipped = CatalogEpoch.counters ()

    Expect.equal
        (skipped.Skipped - verified.Skipped)
        1L
        "The second checkout is vouched for by the token"

    Expect.equal skipped.Verified verified.Verified "The second checkout ran no verification"
    first.Rollback()

    use second = connection.BeginTransaction()
    asOwner connection
    execute connection "ALTER TABLE claimcore.cases DROP CONSTRAINT claimed_money"

    execute
        connection
        "ALTER TABLE claimcore.cases ADD CONSTRAINT claimed_money CHECK (claimed_amount >= 0)"

    asRuntime connection
    let before = CatalogEpoch.counters ()

    for _ in 1..2 do
        requireRefusal connection "A weakened check is refused by the guarded path, every time"

    let after = CatalogEpoch.counters ()

    Expect.equal
        after.Verified
        before.Verified
        "A refused verification is not counted or remembered"

    Expect.equal after.Skipped before.Skipped "A refused checkout is never vouched for"
    second.Rollback()

    use third = connection.BeginTransaction()
    asRuntime connection
    RuntimeDatabase.requireCompatible connection
    third.Rollback()

let private privilegeDriftIsRefused () =
    CatalogEpoch.forget ()
    use connection = openAdmin ()
    use transaction = connection.BeginTransaction()
    asRuntime connection
    RuntimeDatabase.requireCompatible connection
    asOwner connection
    execute connection "GRANT DELETE ON claimcore.cases TO claimcore_app"
    asRuntime connection

    requireRefusal
        connection
        "An extra privilege is refused even though the structure was verified a moment ago"

    transaction.Rollback()

let private cancellableAdmissionIsGuarded () =
    CatalogEpoch.forget ()
    use connection = openAdmin ()
    use transaction = connection.BeginTransaction()
    asRuntime connection
    RuntimeDatabase.requireCompatibleAsync connection |> await
    let verified = CatalogEpoch.counters ()
    RuntimeDatabase.requireCompatibleAsync connection |> await
    let skipped = CatalogEpoch.counters ()
    Expect.equal (skipped.Skipped - verified.Skipped) 1L "The second checkout is vouched for"
    asOwner connection
    execute connection "GRANT DELETE ON claimcore.cases TO claimcore_app"
    asRuntime connection

    Expect.throwsT<RuntimeDatabaseMismatch>
        (fun () -> RuntimeDatabase.requireCompatibleAsync connection |> await)
        "Drift is refused on the asynchronous path"

    transaction.Rollback()

let private runtimeAdmissionTests =
    testList
        "guarded runtime admission"
        [
            testCase
                "[CC-DB-001] the runtime refuses drift the catalog token vouched for before, then admits its reversal"
                weakenedCheckIsRefusedThenReversed
            testCase
                "[CC-DB-001] a privilege drift is refused on the guarded path"
                privilegeDriftIsRefused
            testCase
                "[CC-DB-001] the cancellable runtime admission is vouched for and refuses the same drift"
                cancellableAdmissionIsGuarded
        ]

let tests =
    testList
        "catalog change token behavior"
        [ CatalogEpochTokenTests.tests; mechanismTests; runtimeAdmissionTests ]
