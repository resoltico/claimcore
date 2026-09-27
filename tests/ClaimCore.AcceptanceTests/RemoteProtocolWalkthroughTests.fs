module ClaimCore.AcceptanceTests.RemoteProtocolWalkthroughTests

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.TestSupport

let private requireWitnessedStep (source: byte array) revision command =
    use authored = JsonDocument.Parse(ReadOnlyMemory source)
    let input = authored.RootElement.GetProperty("input")
    let operationId = input.GetProperty("operationId").GetString()
    Expect.equal (input.GetProperty("caseReference").GetString()) "DEMO-0001" "Reserved example"

    Expect.equal
        (input.GetProperty("expectedRevision").GetString())
        (string (revision - 1))
        "Authored revision"

    Expect.equal
        (input.GetProperty("command").GetProperty("kind").GetString())
        command
        "Authored command"

    use response =
        RemoteFixture.run [ "call" ] (Some source)
        |> RemoteFixture.parse 0 "command.execute"

    let outcome = response.RootElement.GetProperty("service").GetProperty("outcome")
    Expect.equal (outcome.GetProperty("tag").GetString()) "COMPLETED" "Definite completion"
    let data = outcome.GetProperty("data")
    Expect.equal (data.GetProperty("settlement").GetString()) "CONFIRMED" "Witness readback"

    Expect.equal
        (data.GetProperty("execution").GetProperty("tag").GetString())
        "ACCEPTED"
        "Accepted"

    let receipt = data.GetProperty("execution").GetProperty("receipt")
    Expect.equal (receipt.GetProperty("operationId").GetString()) operationId "Exact operation"

    Expect.equal
        (receipt.GetProperty("snapshot").GetProperty("revision").GetString())
        (string revision)
        "Exact revision"

    operationId

let private assertCurrent reference =
    use current =
        RemoteFixture.call "case.get" {| caseReference = reference |}
        |> RemoteFixture.parse 0 "case.get"

    let record =
        current.RootElement
            .GetProperty("service")
            .GetProperty("outcome")
            .GetProperty("data")
            .GetProperty("current")
            .GetProperty("case")

    Expect.equal (record.GetProperty("revision").GetString()) "6" "Final revision"

    Expect.equal
        (record.GetProperty("fields").GetProperty("status").GetString())
        "OPENED"
        "Reopened"

    Expect.equal
        (record.GetProperty("fields").GetProperty("paymentDate").ValueKind)
        JsonValueKind.Null
        "The erroneous payment date was cleared; no funds transfer is implied"

let private assertHistory reference operations =
    use history =
        RemoteFixture.call
            "case.history"
            {|
                caseReference = reference
                limit = 50
                detail = "FULL"
            |}
        |> RemoteFixture.parse 0 "case.history"

    let entries =
        history.RootElement
            .GetProperty("service")
            .GetProperty("outcome")
            .GetProperty("data")
            .GetProperty("entries")
            .EnumerateArray()
        |> Seq.toList

    Expect.equal entries.Length 6 "Six accepted records survive the published path"

    for index in 0..5 do
        Expect.equal
            (entries[index].GetProperty("receipt").GetProperty("operationId").GetString())
            (List.item index operations)
            "History retains each exact operation"

let private assertReplay examples =
    use replayed =
        RemoteFixture.run
            [ "call" ]
            (Some(File.ReadAllBytes(Path.Combine(examples, "01-open.json"))))
        |> RemoteFixture.parse 0 "command.execute"

    Expect.equal (RemoteFixture.tag replayed) "OBSERVED_ACCEPTED" "The first exact example replays"

let internal sixRequestWalkthrough () =
    let reference = "DEMO-0001"
    let examples = Path.Combine(RepositoryRoot.find (), "examples")

    let files =
        [
            "01-open.json", "OPEN"
            "02-decide.json", "DECIDE"
            "03-record-payment.json", "RECORD_PAYMENT"
            "04-close.json", "CLOSE"
            "05-reopen.json", "REOPEN"
            "06-correct-payment-record.json", "CLEAR_PAYMENT"
        ]

    let operations =
        files
        |> List.mapi (fun index (filename, command) ->
            File.ReadAllBytes(Path.Combine(examples, filename))
            |> fun source -> requireWitnessedStep source (index + 1) command)

    assertCurrent reference
    assertHistory reference operations
    assertReplay examples
