module ClaimCore.AcceptanceTests.LifecycleTests

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open Npgsql

let private reference = "CC-ACCEPT-001"
let private commandId number = $"30000000-0000-4000-8000-{number:D12}"
let private preparationId number = $"20000000-0000-4000-8000-{number:D12}"

let private registration country claimant insurer amount =
    [
        "incidentDate", "2026-08-01"
        "incidentNotificationDate", "2026-08-03"
        "incidentCountry", country
        "claimantName", claimant
        "insurerName", insurer
        "claimedAmount", amount
        "claimedCurrency", "EUR"
    ]

let private decision amount =
    [
        "paymentDecisionDate", "2026-08-15"
        "payableAmount", amount
        "payableCurrency", "EUR"
    ]

let private outcome (document: JsonDocument) = CliV3Fixtures.outcomeKind document

let private call context endpoint exitCode invocation =
    CliV3Fixtures.call context invocation |> CliV3Fixtures.decode exitCode endpoint

let private expectKind kind (document: JsonDocument) =
    Expect.equal (outcome document) kind "CLI-v3 outcome kind"

let private command context operation revision kind values =
    use response =
        CliV3Fixtures.execute operation reference revision kind values
        |> call context "command.execute" 0

    expectKind "completed" response

let private executeEveryCommand context =
    command
        context
        (commandId 1)
        0L
        "OPEN"
        (registration "Latvia" "Synthetic claimant" "Synthetic insurer" "1000.00")

    command
        context
        (commandId 2)
        1L
        "AMEND_REGISTRATION"
        (registration "Estonia" "Synthetic amended claimant" "Synthetic amended insurer" "1001.00")

    command context (commandId 3) 2L "DECIDE" (decision "750.00")
    command context (commandId 4) 3L "WITHDRAW_DECISION" []
    command context (commandId 5) 4L "DECIDE" (decision "750.00")
    command context (commandId 6) 5L "RECORD_PAYMENT" [ "paymentDate", "2026-08-20" ]
    command context (commandId 7) 6L "CLEAR_PAYMENT" []
    command context (commandId 8) 7L "CLOSE" []
    command context (commandId 9) 8L "REOPEN" []

let private replay context =
    use response =
        CliV3Fixtures.execute (commandId 9) reference 8L "REOPEN" []
        |> call context "command.execute" 0

    expectKind "observedAccepted" response

let private counts (context: DatabaseFixture.Context) =
    use connection = new NpgsqlConnection(context.AdminConnection)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM claimcore.cases), (SELECT count(*) FROM claimcore.case_changes);",
            connection
        )

    use reader = command.ExecuteReader()
    Expect.isTrue (reader.Read()) "Database count row"
    reader.GetInt64(0), reader.GetInt64(1)

let private privateBytes (context: DatabaseFixture.Context) (name: string) (bytes: byte array) =
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
    stream.Write(bytes, 0, bytes.Length)
    stream.Flush(true)
    path

let private digest (document: JsonDocument) =
    document.RootElement
        .GetProperty("outcome")
        .GetProperty("details")
        .GetProperty("summary")
        .GetProperty("requestSha256")
        .GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failtest "Prepared CLI-v3 outcome omitted its digest.")

let private privateRecord (context: DatabaseFixture.Context) (envelope: string) =
    use document = JsonDocument.Parse(File.ReadAllBytes(envelope))

    document.RootElement.GetProperty("canonicalRequestBase64").GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failtest "Exported recovery envelope omitted canonical bytes.")
    |> Convert.FromBase64String
    |> privateBytes context "recovery-record.json"

let private prepare context operation revision kind values =
    use response =
        CliV3Fixtures.prepare operation reference revision kind values
        |> call context "command.prepare" 0

    expectKind "prepared" response
    digest response

let private sessionQualification context preparation digest exportPath =
    let inputs =
        [
            CliV3Fixtures.caseGet reference
            CliV3Fixtures.caseList
            CliV3Fixtures.caseHistory reference "SUMMARY"
            CliV3Fixtures.caseHistory reference "FULL"
            CliV3Fixtures.observe (commandId 9)
            CliV3Fixtures.recoveryList
            CliV3Fixtures.recoveryInspect preparation
            CliV3Fixtures.recoveryResolve preparation digest
            CliV3Fixtures.recoveryExport preparation digest exportPath
        ]

    let expected =
        [
            "case.get", "found"
            "case.list", "succeeded"
            "case.history", "found"
            "case.history", "found"
            "operation.observe", "found"
            "recovery.list", "succeeded"
            "recovery.inspect", "found"
            "recovery.resolve", "completed"
            "recovery.export", "exported"
        ]

    let lines =
        CliV3Fixtures.session context inputs
        |> CliV3Fixtures.sessionLines expected.Length

    List.zip expected lines
    |> List.iter (fun ((endpoint, kind), line) ->
        use response = CliV3Fixtures.decodeLine endpoint line
        expectKind kind response)

let private importArtifacts context envelope record =
    use envelopePreview =
        CliV3Fixtures.importPreview "recovery.importEnvelopePreview" envelope
        |> call context "recovery.importEnvelopePreview" 0

    expectKind "previewed" envelopePreview

    let envelopeDigest =
        envelopePreview.RootElement
            .GetProperty("outcome")
            .GetProperty("preview")
            .GetProperty("sourceSha256")
            .GetString()

    use envelopeRetain =
        CliV3Fixtures.importRetain "recovery.importEnvelopeRetain" envelope envelopeDigest
        |> call context "recovery.importEnvelopeRetain" 0

    expectKind "observedAccepted" envelopeRetain

    use recordPreview =
        CliV3Fixtures.importPreview "recovery.importRecordPreview" record
        |> call context "recovery.importRecordPreview" 0

    expectKind "previewed" recordPreview

    let recordDigest =
        recordPreview.RootElement
            .GetProperty("outcome")
            .GetProperty("preview")
            .GetProperty("sourceSha256")
            .GetString()

    use recordRetain =
        CliV3Fixtures.importRetain "recovery.importRecordRetain" record recordDigest
        |> call context "recovery.importRecordRetain" 0

    expectKind "observedAccepted" recordRetain

let private lifecycle () =
    let context = DatabaseFixture.current ()
    executeEveryCommand context
    replay context
    let closePreparation = preparationId 1
    let closeDigest = prepare context closePreparation 9L "CLOSE" []
    let envelope = Path.Combine(context.TemporaryDirectory, "recovery-envelope.json")
    sessionQualification context closePreparation closeDigest envelope
    Expect.isTrue (File.Exists(envelope)) "Published recovery export was created"
    let reopenPreparation = preparationId 2
    let reopenDigest = prepare context reopenPreparation 10L "REOPEN" []

    use dismissed =
        CliV3Fixtures.recoveryDismiss reopenPreparation reopenDigest
        |> call context "recovery.dismiss" 0

    expectKind "dismissed" dismissed
    let record = privateRecord context envelope
    importArtifacts context envelope record
    Expect.equal (counts context) (1L, 10L) "CLI-v3 replay and recovery preserve history count"
    PublishedExampleWalkthrough.run context

let private rejectedRequestPreservesBusinessState () =
    let context = DatabaseFixture.current ()
    let before = counts context

    use rejected =
        CliV3Fixtures.execute (commandId 10) reference 9L "CLOSE" []
        |> call context "command.execute" 2

    expectKind "rejectedBeforeAttempt" rejected
    Expect.equal (counts context) before "Rejected CLI-v3 command preserves durable business state"

let tests =
    testList
        "published CLI-v3 acceptance"
        [
            testCase
                "[CC-CLI-001] published call and session qualify commands, queries, and recovery"
                lifecycle
            testCase
                "[CC-APP-001] rejected published CLI-v3 command preserves PostgreSQL business state"
                rejectedRequestPreservesBusinessState
            testCase
                "invalid case and recovery cursors, operation IDs, and digests remain typed CLI-v3 failures"
                BoundaryTests.strictBoundary
            testCase
                "invalid UTF-8, escaped Unicode, oversized input, duplicate keys, and trailing documents fail at published CLI framing"
                BoundaryTests.strictBytes
            testCase
                "malformed recovery envelope preview cannot retain preparation bytes"
                BoundaryTests.invalidRecoveryEnvelope
            EndpointModeParityTests.tests
            PrivateFileTests.tests
        ]
    |> testSequenced
