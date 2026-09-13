module ClaimCore.AcceptanceTests.EndpointModeParityTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Expecto
open Npgsql

type private EndpointCheck =
    {
        Endpoint: string
        Invocation: string
        Outcome: string
        ExitCode: int
    }

let private check endpoint outcome exitCode invocation =
    {
        Endpoint = endpoint
        Invocation = invocation
        Outcome = outcome
        ExitCode = exitCode
    }

let private registration =
    [
        "incidentDate", "2026-08-01"
        "incidentNotificationDate", "2026-08-03"
        "incidentCountry", "Latvia"
        "claimantName", "Synthetic parity claimant"
        "insurerName", "Synthetic parity insurer"
        "claimedAmount", "10.25"
        "claimedCurrency", "EUR"
    ]

let private privateSource (context: DatabaseFixture.Context) name =
    let path = Path.Combine(context.TemporaryDirectory, name)

    let options =
        FileStreamOptions(
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        )

    if not (OperatingSystem.IsWindows()) then
        options.UnixCreateMode <- Nullable(UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

    use stream = new FileStream(path, options)
    stream.Write(Encoding.UTF8.GetBytes("{}"))
    stream.Flush(true)
    path

let private commandAndCaseChecks missingReference missingOperation =
    let invalidReference = " "

    [
        check
            "command.prepare"
            "rejected"
            2
            (CliV3Fixtures.prepare
                (Guid.NewGuid().ToString("D"))
                invalidReference
                0L
                "OPEN"
                registration)
        check
            "command.execute"
            "rejectedBeforeAttempt"
            2
            (CliV3Fixtures.execute
                (Guid.NewGuid().ToString("D"))
                invalidReference
                0L
                "OPEN"
                registration)
        check "case.get" "notFound" 2 (CliV3Fixtures.caseGet missingReference)
        check "case.list" "succeeded" 0 CliV3Fixtures.caseList
        check "case.history" "notFound" 2 (CliV3Fixtures.caseHistory missingReference "FULL")
        check "operation.observe" "notFound" 2 (CliV3Fixtures.observe missingOperation)
    ]

let private recoveryChecks (context: DatabaseFixture.Context) missingOperation digest =
    [
        check "recovery.list" "succeeded" 0 CliV3Fixtures.recoveryList
        check "recovery.inspect" "notFound" 2 (CliV3Fixtures.recoveryInspect missingOperation)
        check
            "recovery.resolve"
            "refusedBeforeAttempt"
            2
            (CliV3Fixtures.recoveryResolve missingOperation digest)
        check
            "recovery.dismiss"
            "notFound"
            2
            (CliV3Fixtures.recoveryDismiss missingOperation digest)
        check
            "recovery.export"
            "notFound"
            2
            (CliV3Fixtures.recoveryExport
                missingOperation
                digest
                (Path.Combine(context.TemporaryDirectory, "parity-export-not-created.json")))
    ]

let private importChecks envelope record digest =
    [
        check
            "recovery.importEnvelopePreview"
            "rejected"
            2
            (CliV3Fixtures.importPreview "recovery.importEnvelopePreview" envelope)
        check
            "recovery.importEnvelopeRetain"
            "rejected"
            2
            (CliV3Fixtures.importRetain "recovery.importEnvelopeRetain" envelope digest)
        check
            "recovery.importRecordPreview"
            "rejected"
            2
            (CliV3Fixtures.importPreview "recovery.importRecordPreview" record)
        check
            "recovery.importRecordRetain"
            "rejected"
            2
            (CliV3Fixtures.importRetain "recovery.importRecordRetain" record digest)
    ]

let private checks context =
    let missingReference = "PARITY-MISSING-" + Guid.NewGuid().ToString("N")
    let missingOperation = Guid.NewGuid().ToString("D")
    let envelope = privateSource context "parity-invalid-envelope.json"
    let record = privateSource context "parity-invalid-record.json"

    let digest =
        Encoding.UTF8.GetBytes("{}") |> SHA256.HashData |> Convert.ToHexStringLower

    let all =
        commandAndCaseChecks missingReference missingOperation
        @ recoveryChecks context missingOperation digest
        @ importChecks envelope record digest

    Expect.equal all.Length 15 "Every generated CLI-v3 endpoint has a process frame"
    Expect.equal (all |> List.map _.Endpoint |> Set.ofList |> Set.count) 15 "No duplicate endpoint"
    all

let private durableCounts (context: DatabaseFixture.Context) =
    use connection = new NpgsqlConnection(context.AdminConnection)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM claimcore.cases), "
            + "(SELECT count(*) FROM claimcore.case_changes), "
            + "(SELECT count(*) FROM claimcore.request_preparations)",
            connection
        )

    use reader = command.ExecuteReader()
    Expect.isTrue (reader.Read()) "Synthetic durable-count row"
    reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)

let private callKinds context checks =
    checks
    |> List.map (fun item ->
        let result = CliV3Fixtures.call context item.Invocation
        Expect.equal result.ExitCode item.ExitCode ("Published call exit: " + item.Endpoint)

        use response = result |> CliV3Fixtures.decode item.ExitCode item.Endpoint

        let kind = CliV3Fixtures.outcomeKind response
        Expect.equal kind item.Outcome ("Published call: " + item.Endpoint)
        item.Endpoint, kind)

let private sessionKinds context checks =
    let lines =
        checks
        |> List.map _.Invocation
        |> CliV3Fixtures.session context
        |> CliV3Fixtures.sessionLines checks.Length

    List.zip checks lines
    |> List.map (fun (item, line) ->
        use response = CliV3Fixtures.decodeLine item.Endpoint line
        let kind = CliV3Fixtures.outcomeKind response
        Expect.equal kind item.Outcome ("Published session: " + item.Endpoint)
        item.Endpoint, kind)

let private parity () =
    let context = DatabaseFixture.current ()
    let frames = checks context
    let before = durableCounts context
    let calls = callKinds context frames
    let session = sessionKinds context frames
    Expect.equal session calls "Each endpoint keeps its typed outcome family in both process modes"

    Expect.equal
        (durableCounts context)
        before
        "Rejected and missing probes cannot alter durable state"

let tests =
    testList
        "published CLI-v3 mode parity"
        [
            testCase "all fifteen endpoints preserve typed outcomes through call and session" parity
        ]
