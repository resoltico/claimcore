module ClaimCore.AcceptanceTests.RemoteProtocolBoundaryTests

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.TestSupport
open ClaimCore.AcceptanceTests.RemoteProtocolWalkthroughTests

let private requireProtocolFailure (source: byte array) expectedCode expectedDiagnostic =
    let response = RemoteFixture.run [ "call" ] (Some source)
    Expect.equal response.ExitCode 2 "The published CLI refuses the frame"
    Expect.equal response.StandardError.Length 0 "The refusal has no process diagnostics"
    use document = JsonDocument.Parse(ReadOnlyMemory response.StandardOutput)
    let root = document.RootElement
    Expect.equal (root.GetProperty("kind").GetString()) "protocolFailure" "Typed refusal"
    Expect.equal (root.GetProperty("code").GetString()) expectedCode "Exact safe problem"

    Expect.equal
        (root.GetProperty("diagnosticId").GetString())
        expectedDiagnostic
        "Exact diagnostic identity"

let private registration =
    Map.ofList
        [
            "incidentDate", "2026-09-01"
            "incidentNotificationDate", "2026-09-02"
            "incidentCountry", "Latvia"
            "claimantName", "Synthetic claimant"
            "insurerName", "Synthetic insurer"
            "claimedAmount", "12.34"
            "claimedCurrency", "EUR"
        ]

let private assertUndisclosedCase reference =
    use absent =
        RemoteFixture.call "case.get" {| caseReference = reference |}
        |> RemoteFixture.parse 2 "case.get"

    let outcome = absent.RootElement.GetProperty("service").GetProperty("outcome")

    Expect.equal
        (outcome.GetProperty("tag").GetString())
        "REJECTED"
        "An absent case has the same public refusal as an inaccessible case"

    Expect.equal
        (outcome.GetProperty("data").GetProperty("code").GetString())
        "RESOURCE_UNAVAILABLE"
        "None of the malformed mutation frames reached a readable case"

let private malformedFrames () =
    let reference = "CC-CLI-MALFORMED-" + RemoteFixture.id ()

    let valid =
        RemoteFixture.encode
            "command.execute"
            {|
                operationId = RemoteFixture.id ()
                caseReference = reference
                expectedRevision = "0"
                command =
                    {|
                        kind = "OPEN"
                        values = registration
                    |}
            |}

    let utf8 = Array.append (Encoding.UTF8.GetBytes valid) [| 0xffuy |]

    for source, code, diagnostic in
        [
            utf8, "INVALID_UTF8", "CLI_INVALID_UTF8"
            Encoding.UTF8.GetBytes(valid + "{}"), "INVALID_JSON", "CLI_INVALID_JSON"
            Encoding.UTF8.GetBytes(String.replicate 131073 "x"),
            "INPUT_TOO_LARGE",
            "CLI_DOCUMENT_TOO_LARGE"
            Encoding.UTF8.GetBytes("""{"protocolVersion":4,""" + valid.Substring(1)),
            "DUPLICATE_KEY",
            "CLI_DUPLICATE_PROPERTY"
            Encoding.UTF8.GetBytes(valid.Replace("Synthetic claimant", "\\uD800")),
            "INVALID_UNICODE",
            "CLI_INVALID_UNICODE"
        ] do
        requireProtocolFailure source code diagnostic

    assertUndisclosedCase reference

let private serviceData (document: JsonDocument) =
    document.RootElement
        .GetProperty("service")
        .GetProperty("outcome")
        .GetProperty("data")
        .GetRawText()

let private caseData reference =
    use result =
        RemoteFixture.call "case.get" {| caseReference = reference |}
        |> RemoteFixture.parse 0 "case.get"

    serviceData result

let private historyData reference =
    use result =
        RemoteFixture.call
            "case.history"
            {|
                caseReference = reference
                limit = 50
                detail = "FULL"
            |}
        |> RemoteFixture.parse 0 "case.history"

    serviceData result

let private rejectedCommandKeepsHistory () =
    let reference = "CC-CLI-REJECTED-" + RemoteFixture.id ()
    let openId = RemoteFixture.id ()
    let rejectedId = RemoteFixture.id ()

    use opened =
        RemoteFixture.call
            "command.execute"
            {|
                operationId = openId
                caseReference = reference
                expectedRevision = "0"
                command =
                    {|
                        kind = "OPEN"
                        values = registration
                    |}
            |}
        |> RemoteFixture.parse 0 "command.execute"

    Expect.equal (RemoteFixture.tag opened) "COMPLETED" "Witnessed OPEN completed"
    let beforeCase = caseData reference
    let beforeHistory = historyData reference

    use rejected =
        RemoteFixture.call
            "command.execute"
            {|
                operationId = rejectedId
                caseReference = reference
                expectedRevision = "0"
                command =
                    {|
                        kind = "CLOSE"
                        values = Map.empty<string, string>
                    |}
            |}
        |> RemoteFixture.parse 2 "command.execute"

    let outcome = rejected.RootElement.GetProperty("service").GetProperty("outcome")

    Expect.equal
        (outcome.GetProperty("tag").GetString())
        "REFUSED_BEFORE_ATTEMPT"
        "Stale revision is refused before an attempt"

    Expect.equal
        (outcome.GetProperty("data").GetProperty("rejection").GetProperty("code").GetString())
        "VERSION_CONFLICT"
        "Stale revision has the precise core rejection"

    let afterCase = caseData reference
    let afterHistory = historyData reference

    Expect.equal afterCase beforeCase "Rejected command preserved current case"

    Expect.equal afterHistory beforeHistory "Rejected command added no accepted history"


let private malformedRecoveryIdentity () =
    let operation = "40000000-0000-4000-8000-000000000001"

    for source, code, diagnostic in
        [
            RemoteFixture.encode "operation.observe" {| operationId = "" |}
            |> Encoding.UTF8.GetBytes,
            "INVALID_VALUE",
            "CLI_INVALID_TOKEN"
            RemoteFixture.encode
                "recovery.resolve"
                {|
                    operationId = operation
                    requestSha256 = "bad"
                |}
            |> Encoding.UTF8.GetBytes,
            "INVALID_VALUE",
            "CLI_INVALID_TOKEN"
        ] do
        requireProtocolFailure source code diagnostic

let private malformedPrivateEnvelope () =
    let source =
        Path.Combine(
            RemoteFixture.inputs.Value.PrivateDirectory,
            "malformed-envelope-" + RemoteFixture.id () + ".json"
        )

    let pending () =
        RemoteFixture.call "recovery.list" {| view = "PENDING"; limit = 50 |}
        |> RemoteFixture.parse 0 "recovery.list"

    use before = pending ()

    let writeMalformed () =
        use stream =
            new FileStream(
                source,
                FileStreamOptions(
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = Nullable(UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                )
            )

        stream.Write(Encoding.UTF8.GetBytes("{}"))
        stream.Flush(true)

    try
        writeMalformed ()

        use refused =
            RemoteFixture.call "recovery.importEnvelopePreview" {| source = source |}
            |> RemoteFixture.parse 2 "recovery.importEnvelopePreview"

        Expect.equal (RemoteFixture.tag refused) "REJECTED" "Definite malformed-envelope refusal"

        Expect.isFalse
            (refused.RootElement.GetRawText().Contains(source, StringComparison.Ordinal))
            "The private path is not disclosed"

        use after = pending ()

        let data (document: JsonDocument) =
            document.RootElement
                .GetProperty("service")
                .GetProperty("outcome")
                .GetProperty("data")
                .GetRawText()

        Expect.equal (data after) (data before) "Preview retained no preparation"
    finally
        if File.Exists(source) then
            File.Delete(source)

let tests =
    [
        testCase
            "[CC-CLI-001] published malformed frames retain exact typed refusals"
            malformedFrames
        testCase
            "[CC-APP-001] rejected published CLI-v4 command preserves current case and accepted history"
            rejectedCommandKeepsHistory
        testCase
            "[CC-CLI-001] published six-request witnessed lifecycle retains exact history"
            sixRequestWalkthrough
        testCase
            "[CC-CLI-001] published operation and recovery identities reject invalid UUID and digest"
            malformedRecoveryIdentity
        testCase
            "[CC-CLI-002] malformed owner-private envelope preview retains no preparation"
            malformedPrivateEnvelope
    ]
