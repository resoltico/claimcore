module ClaimCore.IntegrationTests.SchemaTests

open System
open Npgsql
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.IntegrationTests.Fixtures

let private expectSqlState expected action =
    let actual =
        try
            action ()
            None
        with :? PostgresException as error ->
            Some error.SqlState

    Expect.equal
        actual
        (Some expected)
        "PostgreSQL must reject this operation with the expected SQLSTATE"

let private constraintTests =
    testList
        "baseline and constraints"
        [
            testCase "initialization is repeatable only with identical baseline identity" (fun () ->
                let admin = adminConnection ()

                SchemaBaseline.initialize admin "Etc/UTC" syntheticSuppressionCheck
                |> completedAdministration

                SchemaBaseline.initialize admin "Etc/UTC" syntheticSuppressionCheck
                |> completedAdministration)
            testCase "database rejects excessive precision instead of rounding it" (fun () ->
                use database = store ()
                let request = newRequest ()

                Service.executeAsync (database :> IClaimStore) clock request
                |> await
                |> accepted
                |> ignore

                expectSqlState "23514" (fun () ->
                    runSql
                        (appConnection ())
                        "UPDATE claimcore.cases SET claimed_amount = 1.00001 WHERE case_reference = @reference"
                        request.CaseReference))
            testCase "database rejects partial decision tuple" (fun () ->
                use database = store ()
                let request = newRequest ()

                Service.executeAsync (database :> IClaimStore) clock request
                |> await
                |> accepted
                |> ignore

                expectSqlState "23514" (fun () ->
                    runSql
                        (appConnection ())
                        "UPDATE claimcore.cases SET payment_decision_date = DATE '2026-08-15' WHERE case_reference = @reference"
                        request.CaseReference))
            testCase "database rejects payment without decision" (fun () ->
                use database = store ()
                let request = newRequest ()

                Service.executeAsync (database :> IClaimStore) clock request
                |> await
                |> accepted
                |> ignore

                expectSqlState "23514" (fun () ->
                    runSql
                        (appConnection ())
                        "UPDATE claimcore.cases SET payment_date = DATE '2026-08-20' WHERE case_reference = @reference"
                        request.CaseReference))
        ]

let private privilegeTests =
    testList
        "runtime privileges and formats"
        [
            testCase "runtime role cannot rewrite audit history" (fun () ->
                expectSqlState "42501" (fun () ->
                    runSql
                        (appConnection ())
                        "UPDATE claimcore.case_changes SET accepted_actor_id = gen_random_uuid() WHERE case_reference = @reference"
                        "NO-SUCH-CASE"))
            testCase "runtime role cannot mutate the baseline marker" (fun () ->
                expectSqlState "42501" (fun () ->
                    runSql
                        (appConnection ())
                        "UPDATE claimcore.schema_baseline SET baseline_id = baseline_id WHERE @reference = @reference"
                        "NO-SUCH-CASE"))
            testCase "runtime role cannot delete cases" (fun () ->
                expectSqlState "42501" (fun () ->
                    runSql
                        (appConnection ())
                        "DELETE FROM claimcore.cases WHERE case_reference = @reference"
                        "NO-SUCH-CASE"))
            testCase
                "accepted receipts retain explicit request and snapshot format revisions"
                (fun () ->
                    use database = store ()
                    let request = newRequest ()

                    Service.executeAsync (database :> IClaimStore) clock request
                    |> await
                    |> accepted
                    |> ignore

                    use connection = new NpgsqlConnection(appConnection ())
                    connection.Open()

                    use command =
                        new NpgsqlCommand(
                            "SELECT request_format_version, snapshot_version FROM claimcore.case_changes WHERE operation_id = @operation",
                            connection
                        )

                    command.Parameters.AddWithValue("operation", request.OperationId) |> ignore
                    use reader = command.ExecuteReader()
                    Expect.isTrue (reader.Read()) "Receipt exists"

                    Expect.equal
                        (reader.GetInt16(0), reader.GetInt16(1))
                        (int16 RecordVersions.RequestFingerprint, int16 RecordVersions.Snapshot)
                        "Independent formats")
        ]

let private withFailureTrigger (admin: NpgsqlConnection) reference action =
    let token = "test_fault_" + Guid.NewGuid().ToString("N")
    // Interpolated identifiers/reference are generated ASCII test values, never external input.
    let ddl =
        $"CREATE FUNCTION claimcore.{token}() RETURNS trigger LANGUAGE plpgsql AS $body$ BEGIN IF NEW.case_reference = '{reference}' THEN RAISE EXCEPTION 'injected test failure'; END IF; RETURN NEW; END; $body$; CREATE TRIGGER {token} BEFORE INSERT ON claimcore.case_changes FOR EACH ROW EXECUTE FUNCTION claimcore.{token}();"

    use install = new NpgsqlCommand(ddl, admin)
    install.ExecuteNonQuery() |> ignore

    try
        action ()
    finally
        use remove =
            new NpgsqlCommand(
                $"DROP TRIGGER {token} ON claimcore.case_changes; DROP FUNCTION claimcore.{token}();",
                admin
            )

        remove.ExecuteNonQuery() |> ignore

let private requireOwnerRollback (admin: NpgsqlConnection) reference =
    use transaction = admin.BeginTransaction()

    try
        use update =
            new NpgsqlCommand(
                "UPDATE claimcore.cases SET status = 'CLOSED', revision = 2 WHERE case_reference = @reference",
                admin,
                transaction
            )

        update.Parameters.AddWithValue("reference", reference) |> ignore
        Expect.equal (update.ExecuteNonQuery()) 1 "The synthetic case was updated"

        expectSqlState "P0001" (fun () ->
            use insert =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.case_changes (operation_id, case_reference) VALUES (gen_random_uuid(), @reference)",
                    admin,
                    transaction
                )

            insert.Parameters.AddWithValue("reference", reference) |> ignore
            insert.ExecuteNonQuery() |> ignore)
    finally
        transaction.Rollback()

let private requireOriginalState (service: IClaimStore) reference =
    let current = service.Get(reference) |> await |> accepted |> Option.map Claim.view

    Expect.equal
        (current |> Option.map (fun value -> value.Version, value.Fields.Status))
        (Some(1L, CaseStatus.Opened))
        "The failed update transaction rolled back"

    let history = service.History(reference, 0L) |> await |> accepted
    Expect.equal history.Items.Length 1 "No partial history; normal reads resumed"

let private atomicityTests =
    testList
        "atomic persistence"
        [
            testCase "schema drift is refused and failed owner transaction rolls back" (fun () ->
                use database = store ()
                let service = database :> IClaimStore
                let request = newRequest ()
                Service.executeAsync service clock request |> await |> accepted |> ignore
                use admin = new NpgsqlConnection(adminConnection ())
                admin.Open()

                withFailureTrigger admin request.CaseReference (fun () ->
                    Expect.throwsT<RuntimeDatabaseMismatch>
                        (fun () ->
                            Service.executeAsync service clock (next request 1L Command.Close)
                            |> await
                            |> ignore)
                        "An unregistered trigger refuses runtime admission"

                    requireOwnerRollback admin request.CaseReference)

                requireOriginalState service request.CaseReference)
        ]

let tests =
    testList
        "PostgreSQL schema and failure boundary"
        [ constraintTests; privilegeTests; atomicityTests ]
