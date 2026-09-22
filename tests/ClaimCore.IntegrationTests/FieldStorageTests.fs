module ClaimCore.IntegrationTests.FieldStorageTests

open System
open Npgsql
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures

let private expectedBusinessColumns =
    [
        "incident_date", "date", "NO"
        "incident_notification_date", "date", "NO"
        "incident_country", "text", "NO"
        "claimant_name", "text", "NO"
        "insurer_name", "text", "NO"
        "claimed_amount", "numeric", "NO"
        "claimed_currency", "text", "NO"
        "case_reference", "text", "NO"
        "payment_decision_date", "date", "YES"
        "payable_amount", "numeric", "YES"
        "payable_currency", "text", "YES"
        "payment_date", "date", "YES"
        "status", "text", "NO"
    ]

let private actualBusinessColumns (connection: NpgsqlConnection) =
    use command =
        new NpgsqlCommand(
            "SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_schema = 'claimcore' AND table_name = 'cases' AND column_name <> 'revision' ORDER BY ordinal_position",
            connection
        )

    use reader = command.ExecuteReader()

    [
        while reader.Read() do
            reader.GetString(0), reader.GetString(1), reader.GetString(2)
    ]

let private revisionColumn (connection: NpgsqlConnection) =
    use command =
        new NpgsqlCommand(
            "SELECT data_type, is_nullable FROM information_schema.columns WHERE table_schema = 'claimcore' AND table_name = 'cases' AND column_name = 'revision'",
            connection
        )

    use reader = command.ExecuteReader()

    if reader.Read() then
        Some(reader.GetString(0), reader.GetString(1))
    else
        None

let private verifyCurrentSchema () =
    use connection = new NpgsqlConnection(appConnection ())
    connection.Open()
    Expect.equal expectedBusinessColumns.Length 13 "No extra claim entries"

    Expect.equal
        (actualBusinessColumns connection)
        expectedBusinessColumns
        "Real PostgreSQL business columns"

    Expect.equal (revisionColumn connection) (Some("bigint", "NO")) "Atomic revision metadata"

let private expectedFields reference : CaseFields =
    {
        IncidentDate = "2026-08-01"
        IncidentNotificationDate = "2026-08-03"
        IncidentCountry = "Lithuania"
        ClaimantName = "Integration Test Company"
        InsurerName = "Alleged Test Insurer"
        ClaimedAmount = "1000"
        ClaimedCurrency = "EUR"
        CaseReference = reference
        PaymentDecisionDate = Some "2026-08-15"
        PayableAmount = Some "750.125"
        PayableCurrency = Some "USD"
        PaymentDate = Some "2026-08-20"
        Status = CaseStatus.Closed
    }

let private persistLifecycle initial =
    let decision =
        Command.Decide
            {
                PaymentDecisionDate = "2026-08-15"
                PayableAmount = "750.1250"
                PayableCurrency = "USD"
            }

    use database = store ()
    let service = database :> IClaimStore

    for command in
        [
            initial
            next initial 1L decision
            next initial 2L (Command.RecordPayment "2026-08-20")
            next initial 3L Command.Close
        ] do
        Service.executeAsync service clock command |> await |> accepted |> ignore

let private verifyLifecycle () =
    let initial = newRequest ()
    persistLifecycle initial
    use reopened = store ()

    let actual =
        (reopened :> IClaimStore).Get(initial.CaseReference)
        |> await
        |> accepted
        |> Option.defaultWith (fun () -> failtest "Persisted case missing")
        |> Claim.view

    Expect.isTrue
        (actual.Fields = expectedFields initial.CaseReference)
        "Every requested value survived"

    Expect.equal actual.Version 4L "Concurrency is separate metadata"

let private installedMigrations () =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT version, name, script_sha256 FROM claimcore.schema_migrations ORDER BY version",
            connection
        )

    use reader = command.ExecuteReader()

    [
        while reader.Read() do
            reader.GetInt32(0), reader.GetString(1), reader.GetString(2)
    ]

let private baselineTests =
    testList
        "baseline and current schema"
        [
            testCase "baseline rejects older releases and unqualified next major" (fun () ->
                for rejected in [ 0; 170011; 180000; 180005; 190000; 200000 ] do
                    Expect.isFalse (Baseline.isSupported rejected) "Unsupported server"

                for accepted in [ 180006; 180007; 180099 ] do
                    Expect.isTrue (Baseline.isSupported accepted) "Minor updates in the same major"

                Expect.equal
                    Baseline.containerImage
                    "ghcr.io/resoltico/claimcore-postgres:18.6-trixie-p2-r34736975281-1@sha256:ae84d4fd380ade930b6246340a39e2448786f5bcaad258ee1f2e62807a5f8c45"
                    "Digest-pinned multi-platform image")
            testCase "actual server is on the required baseline" (fun () ->
                use connection = new NpgsqlConnection(appConnection ())
                connection.Open()
                Baseline.requireCompatible connection)
            testCase
                "live current-state row keeps exactly thirteen business columns plus revision metadata"
                verifyCurrentSchema
        ]

let private persistenceTests =
    testList
        "field persistence and migration journal"
        [
            testCase
                "all fields survive decision, payment and closure without currency coercion"
                verifyLifecycle
            testCase "migration journal exactly matches the ordered embedded manifest" (fun () ->
                Migrations.apply (adminConnection ()) |> completedAdministration

                let expected =
                    SchemaDefinition.all ()
                    |> List.map (fun migration ->
                        migration.Version, migration.Name, migration.Digest)

                Expect.equal
                    (installedMigrations ())
                    expected
                    "Every installed migration is ordered and checksum-bound")
        ]

let tests =
    testList "basic schema and PostgreSQL 18.6 baseline" [ baselineTests; persistenceTests ]
