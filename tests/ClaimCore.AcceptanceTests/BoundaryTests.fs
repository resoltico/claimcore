module ClaimCore.AcceptanceTests.BoundaryTests

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open Npgsql

let private frame endpoint input =
    JsonSerializer.Serialize(
        {|
            protocolVersion = 3
            endpoint = endpoint
            input = input
        |}
    )

let private expectProtocolFailure context expectedCode invocation =
    let result = CliV3Fixtures.call context invocation
    Expect.equal result.ExitCode 2 "Invalid CLI-v3 frame exits as protocol failure"
    Expect.equal result.StandardError.Length 0 "Invalid input cannot enter diagnostics"
    use document = JsonDocument.Parse(result.StandardOutput)
    let root = document.RootElement
    Expect.equal (root.GetProperty("kind").GetString()) "protocolFailure" "Frame failure kind"
    Expect.equal (root.GetProperty("code").GetString()) expectedCode "Exact decoder code"

let private expectProtocolBytes context expectedCode bytes =
    let result = CliV3Fixtures.callBytes context bytes
    Expect.equal result.ExitCode 2 "Invalid input bytes exit as protocol failure"
    Expect.equal result.StandardError.Length 0 "Rejected bytes cannot enter diagnostics"
    use document = JsonDocument.Parse(result.StandardOutput)
    let root = document.RootElement
    Expect.equal (root.GetProperty("kind").GetString()) "protocolFailure" "Byte failure kind"

    Expect.equal
        (root.GetProperty("code").GetString())
        expectedCode
        "Exact byte limit or parser code"

let private expectRejected context endpoint invocation =
    use response =
        invocation |> CliV3Fixtures.call context |> CliV3Fixtures.decode 2 endpoint

    Expect.equal (CliV3Fixtures.outcomeKind response) "rejected" "Typed cursor rejection"

let strictBoundary () =
    let context = DatabaseFixture.current ()

    frame
        "case.history"
        {|
            caseReference = "CC-ACCEPT-001"
            cursor = "not-a-history-cursor"
            limit = 50
            detail = "FULL"
        |}
    |> expectRejected context "case.history"

    frame
        "recovery.list"
        {|
            cursor = "not-a-recovery-cursor"
            limit = 50
        |}
    |> expectRejected context "recovery.list"

    let zero = Guid.Empty.ToString("D")
    let operation = Guid.NewGuid().ToString("D")
    let digest = String.replicate 64 "a"

    frame "operation.observe" {| operationId = zero |}
    |> expectProtocolFailure context "INVALID_UUID"

    frame
        "recovery.resolve"
        {|
            operationId = zero
            requestSha256 = digest
        |}
    |> expectProtocolFailure context "INVALID_UUID"

    frame
        "recovery.resolve"
        {|
            operationId = operation
            requestSha256 = "bad"
        |}
    |> expectProtocolFailure context "INVALID_DIGEST"

let strictBytes () =
    let context = DatabaseFixture.current ()
    expectProtocolBytes context "INVALID_UTF8" [| 0xFFuy |]
    expectProtocolBytes context "INPUT_TOO_LARGE" (Array.create 131073 (byte 'x'))
    expectProtocolBytes context "INVALID_JSON" (Encoding.UTF8.GetBytes("{}{}"))

    let duplicate =
        """{"protocolVersion":3,"protocolVersion":3,"endpoint":"case.list","input":{"limit":1}}"""

    expectProtocolBytes context "DUPLICATE_KEY" (Encoding.UTF8.GetBytes(duplicate))

    let invalidValue =
        """{"protocolVersion":3,"endpoint":"case.get","input":{"caseReference":"\uD800"}}"""

    expectProtocolBytes context "INVALID_UNICODE" (Encoding.UTF8.GetBytes(invalidValue))
    expectProtocolBytes context "INVALID_UNICODE" (Encoding.UTF8.GetBytes("""{"\uD800":1}"""))

let private preparationCount connectionString =
    use connection = new NpgsqlConnection(connectionString)
    connection.Open()

    use command =
        new NpgsqlCommand("SELECT count(*) FROM claimcore.request_preparations", connection)

    command.ExecuteScalar() :?> int64

let invalidRecoveryEnvelope () =
    let context = DatabaseFixture.current ()

    let source =
        Path.Combine(context.TemporaryDirectory, "malformed-recovery-envelope.json")

    let options =
        FileStreamOptions(
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        )

    if not (OperatingSystem.IsWindows()) then
        options.UnixCreateMode <- Nullable(UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

    do
        use stream = new FileStream(source, options)
        stream.Write(Encoding.UTF8.GetBytes("not-json"))
        stream.Flush(true)

    let before = preparationCount context.AdminConnection

    use response =
        CliV3Fixtures.importPreview "recovery.importEnvelopePreview" source
        |> CliV3Fixtures.call context
        |> CliV3Fixtures.decode 2 "recovery.importEnvelopePreview"

    Expect.equal (CliV3Fixtures.outcomeKind response) "rejected" "Invalid envelope is refused"
    Expect.equal (preparationCount context.AdminConnection) before "Preview cannot retain bytes"
