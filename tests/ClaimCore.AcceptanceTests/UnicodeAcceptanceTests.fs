module ClaimCore.AcceptanceTests.UnicodeAcceptanceTests

open System
open System.Text
open System.IO
open System.Text.Json
open System.Text.Encodings.Web
open Expecto

let private outcome endpoint input =
    use reply = RemoteFixture.call endpoint input |> RemoteFixture.parse 0 endpoint
    reply.RootElement.GetProperty("service").GetProperty("outcome").Clone()

let private command id reference revision kind values =
    {|
        operationId = id
        caseReference = reference
        expectedRevision = revision
        command = {| kind = kind; values = values |}
    |}

let private recorded (value: JsonElement) revision =
    Expect.equal (value.GetProperty("tag").GetString()) "COMPLETED" "Definite published execution"
    let data = value.GetProperty("data")

    Expect.equal
        (data.GetProperty("settlement").GetString())
        "CONFIRMED"
        "Independent witness readback"

    let execution = data.GetProperty("execution")

    Expect.equal
        (execution.GetProperty("tag").GetString())
        "ACCEPTED"
        "Valid Unicode reply is admitted"

    let receipt = execution.GetProperty("receipt")

    Expect.equal
        (receipt.GetProperty("snapshot").GetProperty("revision").GetString())
        revision
        "One exact revision"

    receipt

let private readBack reference id digest =
    let current = outcome "case.get" {| caseReference = reference |}

    Expect.equal
        (current
            .GetProperty("data")
            .GetProperty("current")
            .GetProperty("case")
            .GetProperty("revision")
            .GetString())
        "2"
        "Replay did not add a revision"

    let history =
        outcome
            "case.history"
            {|
                caseReference = reference
                detail = "FULL"
                limit = 50
            |}

    Expect.equal
        (history.GetProperty("data").GetProperty("entries").GetArrayLength())
        2
        "Full Unicode history is admitted"

    let inspected =
        outcome
            "recovery.inspect"
            {|
                operationId = id
                attemptLimit = 50
            |}

    Expect.equal
        (inspected.GetProperty("tag").GetString())
        "SUCCEEDED"
        "Unicode preparation and attempt context remains readable"

    Expect.equal
        (inspected
            .GetProperty("data")
            .GetProperty("value")
            .GetProperty("value")
            .GetProperty("preparation")
            .GetProperty("summary")
            .GetProperty("requestSha256")
            .GetString())
        digest
        "Exact recovery request identity is preserved"


let private scalarInput id reference name =
    let values =
        {|
            incidentDate = "2026-08-01"
            incidentNotificationDate = "2026-08-03"
            incidentCountry = String.replicate 100 "🙂"
            claimantName = name
            insurerName = name
            claimedAmount = "1.0000"
            claimedCurrency = "EUR"
        |}

    command id reference "0" "OPEN" values

let private literalCall input =
    let options =
        JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let serialized =
        JsonSerializer.Serialize(
            {|
                protocolVersion = 4
                endpoint = "command.execute"
                input = input
            |},
            options
        )

    let literal =
        serialized.Replace("\\uD83D\\uDE42", "🙂", StringComparison.OrdinalIgnoreCase)

    Expect.isTrue
        (literal.Contains("🙂", StringComparison.Ordinal))
        "Input contains literal supplementary UTF-8"

    Expect.isFalse
        (literal.Contains("\\uD83D", StringComparison.OrdinalIgnoreCase))
        "Selected supplementary scalar is not escaped"

    Encoding.UTF8.GetBytes literal

let private exactFields (receipt: JsonElement) reference name =
    let fields = receipt.GetProperty("snapshot").GetProperty("fields")

    for field, expected in
        [
            "caseReference", reference
            "incidentCountry", String.replicate 100 "🙂"
            "claimantName", name
            "insurerName", name
        ] do
        Expect.equal
            (fields.GetProperty(field).GetString())
            expected
            "Accepted supplementary business text remains exact"

let private supplementaryRoundTrip () =
    let reference = Guid.NewGuid().ToString("N") + String.replicate 48 "🙂"
    let id = Guid.NewGuid().ToString("D")
    let name = String.replicate 200 "🙂"

    let input = scalarInput id reference name
    let prepared = outcome "command.prepare" input

    Expect.equal
        (prepared.GetProperty("tag").GetString())
        "PREPARED"
        "Escaped supplementary input is accepted"

    let digest =
        prepared
            .GetProperty("data")
            .GetProperty("details")
            .GetProperty("summary")
            .GetProperty("requestSha256")
            .GetString()

    let literal = literalCall input

    use executed =
        RemoteFixture.run [ "call" ] (Some literal)
        |> RemoteFixture.parse 0 "command.execute"

    let accepted = executed.RootElement.GetProperty("service").GetProperty("outcome")
    recorded accepted "1" |> ignore
    let close = command (Guid.NewGuid().ToString("D")) reference "1" "CLOSE" {| |}
    let receipt = recorded (outcome "command.execute" close) "2"

    exactFields receipt reference name

    let replay = outcome "command.execute" close

    Expect.equal
        (replay.GetProperty("tag").GetString())
        "OBSERVED_ACCEPTED"
        "Exact accepted replay is observed"

    readBack reference id digest

let private observed id reference name =
    let value = outcome "operation.observe" {| operationId = id |}

    Expect.equal
        (value.GetProperty("tag").GetString())
        "SUCCEEDED"
        "Published receipt observation is admitted"

    let found = value.GetProperty("data")

    Expect.equal
        (found.GetProperty("tag").GetString())
        "FOUND"
        "Exact accepted operation is observed"

    let receipt = found.GetProperty("receipt")

    Expect.equal
        (receipt.GetProperty("operationId").GetString())
        id
        "Observed receipt has the exact requested identity"

    exactFields receipt reference name

let private webAuthoredRoundTrip () =
    let path =
        Path.Combine(
            RemoteFixture.inputs.Value.PrivateDirectory,
            "playwright-private",
            "unicode-case.json"
        )

    use binding = JsonDocument.Parse(File.ReadAllText(path))
    let source = binding.RootElement

    let text (name: string) =
        source.GetProperty(name).GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "A same-run Unicode binding member is missing.")

    let reference = text "reference"
    let id = text "operationId"
    let digest = text "requestSha256"
    let name = String.replicate 200 "🙂"
    let current = outcome "case.get" {| caseReference = reference |}

    Expect.equal
        (current.GetProperty("tag").GetString())
        "SUCCEEDED"
        "Web-authored current reply is admitted"

    let currentCase =
        current.GetProperty("data").GetProperty("current").GetProperty("case")

    Expect.equal
        (currentCase.GetProperty("revision").GetString())
        "1"
        "Web accepted exactly one opening"

    observed id reference name
    let close = command (Guid.NewGuid().ToString("D")) reference "1" "CLOSE" {| |}
    exactFields (recorded (outcome "command.execute" close) "2") reference name

    Expect.equal
        ((outcome "command.execute" close).GetProperty("tag").GetString())
        "OBSERVED_ACCEPTED"
        "Exact accepted ASCII replay remains definite"

    readBack reference id digest

let tests =
    [
        testCase
            "[CC-CLI-001][CC-REC-001] published supplementary Unicode input and ASCII mutation replies preserve one exact replay"
            supplementaryRoundTrip
        testCase
            "[CC-CLI-001][CC-REC-001] Web-authored supplementary boundaries survive published native reads and ASCII acceptance exactly once"
            webAuthoredRoundTrip
    ]
