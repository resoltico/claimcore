module ClaimCore.IntegrationTests.StorageBoundaryTests

open System
open System.IO
open System.Transactions
open Npgsql
open NpgsqlTypes
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures

let private rejectDriftedAmount (value: string) =
    use database = store ()
    let request = newRequest ()

    Service.executeAsync (database :> IClaimStore) clock request
    |> await
    |> accepted
    |> ignore
    // Deliberately malformed data is visible only inside this owner transaction, rolled back below.
    // This proves the row decoder does not depend exclusively on a constraint remaining present.
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()
    use transaction = connection.BeginTransaction()

    try
        use drop =
            new NpgsqlCommand(
                "ALTER TABLE claimcore.cases DROP CONSTRAINT claimed_money",
                connection,
                transaction
            )

        drop.ExecuteNonQuery() |> ignore

        use corrupt =
            new NpgsqlCommand(
                "UPDATE claimcore.cases SET claimed_amount = CAST(@amount AS numeric) WHERE case_reference = @reference",
                connection,
                transaction
            )

        let amount = corrupt.Parameters.Add("amount", NpgsqlDbType.Text)
        amount.Value <- value
        let reference = corrupt.Parameters.Add("reference", NpgsqlDbType.Text)
        reference.Value <- request.CaseReference
        corrupt.ExecuteNonQuery() |> ignore
        use query = new NpgsqlCommand(Sql.selectCase, connection, transaction)
        query.Parameters.AddWithValue("reference", request.CaseReference) |> ignore
        use reader = query.ExecuteReader()
        Expect.isTrue (reader.Read()) "Synthetic row exists"

        let rejected =
            try
                Rows.claim reader |> ignore
                false
            with :? InvalidDataException ->
                true

        Expect.isTrue rejected "Invalid amount must not be rounded/clamped before domain validation"
        reader.Close()
    finally
        transaction.Rollback()

let private withRoleSetting (setting: string) (value: string) action =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()

    use apply =
        new NpgsqlCommand($"ALTER ROLE claimcore_app SET {setting} TO {value}", connection)

    apply.ExecuteNonQuery() |> ignore

    try
        action ()
    finally
        use reset =
            new NpgsqlCommand($"ALTER ROLE claimcore_app RESET {setting}", connection)

        reset.ExecuteNonQuery() |> ignore

let private scalarTests =
    testList
        "storage scalars and fixture target"
        [
            testCase "integration setup refuses different test endpoints" (fun () ->
                let app = "Host=localhost;Database=basic_test;Username=claimcore_app"
                let owner = "Host=localhost;Database=basic_test;Username=postgres"
                requireSameTarget app owner

                for other in
                    [
                        "Host=other;Database=basic_test;Username=postgres"
                        "Host=localhost;Port=6432;Database=basic_test;Username=postgres"
                        "Host=localhost;Database=other_test;Username=postgres"
                        "Host=localhost;Database=basic_test;Username=claimcore_app"
                        "Host=localhost;Database=basic_test"
                    ] do
                    Expect.throws
                        (fun () -> requireSameTarget app other)
                        "Reject mismatched owner target")
            testCase "date day encoding has exact limits" (fun () ->
                for value in
                    [ "0001-01-01"; "1900-03-01"; "2000-02-29"; "2026-09-07"; "9999-12-31" ] do
                    Expect.equal
                        (ScalarEncoding.dateDays value |> ScalarEncoding.dateText)
                        value
                        "Exact date"

                for invalid in [ Int32.MinValue; Int32.MaxValue; -730120; 2921940 ] do
                    Expect.throws
                        (fun () -> ScalarEncoding.dateText invalid |> ignore)
                        "Reject outside finite range")
            testCase "excess precision is rejected without readback rounding" (fun () ->
                rejectDriftedAmount "1.00001")
            testCase "out of decimal range is rejected without conversion overflow" (fun () ->
                rejectDriftedAmount "10000000000000000000000000000000000000000")
            testCase "non-finite stored money is refused" (fun () -> rejectDriftedAmount "NaN")
        ]

let private environmentTests =
    testList
        "database environment"
        [
            testCase "DateStyle cannot change date persistence" (fun () ->
                withRoleSetting "DateStyle" "'SQL, DMY'" (fun () ->
                    use database = new PostgresStore(appConnection ())
                    let service = database :> IClaimStore
                    let request = newRequest ()
                    let original = Service.executeAsync service clock request |> await |> accepted

                    let current =
                        Service.getAsync service request.CaseReference
                        |> await
                        |> accepted
                        |> Option.defaultWith (fun () -> failtest "Expected the stored case.")

                    Expect.isTrue
                        (Claim.view current = Claim.view original.Case)
                        "No date string conversion depends on session style"))
            testCase "async commit and read-only sessions are refused" (fun () ->
                for setting, value in
                    [ "synchronous_commit", "off"; "default_transaction_read_only", "on" ] do
                    withRoleSetting setting value (fun () ->
                        use database = new PostgresStore(appConnection ())

                        Expect.equal
                            (database.CheckSchema() |> await)
                            (Error CoreFailure.SchemaMismatch)
                            "Connection settings must meet baseline"))
            testCase "migration refuses startup-option overrides" (fun () ->
                let builder = NpgsqlConnectionStringBuilder(adminConnection ())
                builder.Options <- "-c synchronous_commit=off"

                Expect.throwsT<ArgumentException>
                    (fun () -> Migrations.apply builder.ConnectionString)
                    "No DDL session accepts startup overrides")
        ]

let private transactionTests =
    testList
        "transaction and migration integrity"
        [
            testCase "core transaction does not join caller ambient scope" (fun () ->
                let request = newRequest ()

                let submit () =
                    use _ambient = new TransactionScope()
                    use database = store ()

                    Service.executeAsync (database :> IClaimStore) clock request
                    |> await
                    |> accepted
                    |> ignore
                    // No Complete: disposal must not roll back a core-owned, already acknowledged commit.
                    ()

                submit ()
                use database = store ()

                let current =
                    Service.getAsync (database :> IClaimStore) request.CaseReference
                    |> await
                    |> accepted

                Expect.isSome current "Commit ownership stays inside core/store boundary")
            testCase "runtime rejects installed script hash mismatch" (fun () ->
                let original = (SchemaDefinition.all ()).Head.Digest
                use connection = new NpgsqlConnection(adminConnection ())
                connection.Open()

                let set value =
                    use update =
                        new NpgsqlCommand(
                            "UPDATE claimcore.schema_migrations SET script_sha256 = @value WHERE version = 1",
                            connection
                        )

                    let parameter = update.Parameters.Add("value", NpgsqlDbType.Text)
                    parameter.Value <- value
                    update.ExecuteNonQuery() |> ignore

                try
                    set (String.replicate 64 "0")

                    Expect.throwsT<InvalidDataException>
                        (fun () -> Migrations.apply (adminConnection ()))
                        "Migrator never rewrites an adopted checksum"

                    use database = new PostgresStore(appConnection ())

                    Expect.equal
                        (database.CheckSchema() |> await)
                        (Error CoreFailure.SchemaMismatch)
                        "Same integer schema version is insufficient"
                finally
                    set original)
        ]

let tests =
    testList
        "Storage boundary qualification"
        [ StorageDateTests.tests; scalarTests; environmentTests; transactionTests ]
