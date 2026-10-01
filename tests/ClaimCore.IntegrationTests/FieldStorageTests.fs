module ClaimCore.IntegrationTests.FieldStorageTests

open System
open Npgsql
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.TestSupport
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
            "SELECT column_name, data_type, is_nullable FROM information_schema.columns "
            + "WHERE table_schema = 'claimcore' AND table_name = 'cases' "
            + "AND column_name NOT IN ('revision','case_id','disposition','privacy_phase',"
            + "'lifecycle_sequence','lifecycle_event_hash') ORDER BY ordinal_position",
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

let private caseIdColumn (connection: NpgsqlConnection) =
    use command =
        new NpgsqlCommand(
            "SELECT data_type, is_nullable FROM information_schema.columns WHERE table_schema = 'claimcore' AND table_name = 'cases' AND column_name = 'case_id'",
            connection
        )

    use reader = command.ExecuteReader()

    if reader.Read() then
        Some(reader.GetString(0), reader.GetString(1))
    else
        None

let private lifecycleColumns (connection: NpgsqlConnection) =
    use command =
        new NpgsqlCommand(
            "SELECT column_name,data_type,is_nullable FROM information_schema.columns "
            + "WHERE table_schema='claimcore' AND table_name='cases' "
            + "AND column_name IN ('disposition','privacy_phase','lifecycle_sequence',"
            + "'lifecycle_event_hash') ORDER BY ordinal_position",
            connection
        )

    use reader = command.ExecuteReader()

    [
        while reader.Read() do
            reader.GetString(0), reader.GetString(1), reader.GetString(2)
    ]

let private lifecycleConstraints (connection: NpgsqlConnection) =
    use command =
        new NpgsqlCommand(
            "SELECT conname,contype::text,convalidated,conenforced FROM pg_catalog.pg_constraint "
            + "WHERE conrelid='claimcore.cases'::regclass AND conname IN ("
            + "'cases_disposition_check','cases_privacy_phase_check',"
            + "'cases_lifecycle_sequence_check','cases_lifecycle_event_hash_check') "
            + "ORDER BY conname",
            connection
        )

    use reader = command.ExecuteReader()

    [
        while reader.Read() do
            reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)
    ]

let private verifyCurrentSchema () =
    use connection = new NpgsqlConnection(appConnection ())
    connection.Open()
    Expect.equal expectedBusinessColumns.Length 13 "No extra claim entries"

    Expect.equal
        (actualBusinessColumns connection)
        expectedBusinessColumns
        "Real PostgreSQL business columns"

    Expect.equal (revisionColumn connection) (Some("bigint", "NO")) "Atomic revision metadata"
    Expect.equal (caseIdColumn connection) (Some("uuid", "NO")) "Opaque case identity metadata"

    Expect.equal
        (lifecycleColumns connection)
        [
            "disposition", "text", "NO"
            "privacy_phase", "text", "NO"
            "lifecycle_sequence", "bigint", "NO"
            "lifecycle_event_hash", "bytea", "NO"
        ]
        "Lifecycle authority remains technical metadata outside the thirteen business fields"

    Expect.equal
        (lifecycleConstraints connection)
        [
            "cases_disposition_check", "c", true, true
            "cases_lifecycle_event_hash_check", "c", true, true
            "cases_lifecycle_sequence_check", "c", true, true
            "cases_privacy_phase_check", "c", true, true
        ]
        "Every lifecycle shape constraint is validated and enforced"

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
        CommandExecution.executeAsync service clock command
        |> await
        |> accepted
        |> ignore

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

let private installedBaseline () =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT baseline_id, script_sha256 FROM claimcore.schema_baseline",
            connection
        )

    use reader = command.ExecuteReader()

    [
        while reader.Read() do
            reader.GetString(0), reader.GetString(1)
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

                Expect.isTrue
                    (System.Text.RegularExpressions.Regex.IsMatch(
                        Baseline.containerImage,
                        $"^postgres:{Baseline.minimumVersion}@sha256:[0-9a-f]{{64}}$"
                    ))
                    "The official image of the selected release, pinned by digest")
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
        "field persistence and baseline identity"
        [
            testCase
                "all fields survive decision, payment and closure without currency coercion"
                verifyLifecycle
            testCase "baseline marker exactly matches the embedded baseline identity" (fun () ->
                SchemaBaseline.initialize (adminConnection ()) "Etc/UTC" syntheticSuppressionCheck
                |> completedAdministration

                let baseline = SchemaDefinition.current ()
                let expected = [ baseline.Id, baseline.Digest ]

                Expect.equal
                    (installedBaseline ())
                    expected
                    "The installation has exactly one checksum-bound baseline marker")
        ]

let tests =
    testList "basic schema and PostgreSQL 18.6 baseline" [ baselineTests; persistenceTests ]
