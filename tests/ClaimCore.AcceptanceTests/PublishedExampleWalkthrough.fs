module ClaimCore.AcceptanceTests.PublishedExampleWalkthrough

open System.IO
open System.Text.Json
open Expecto
open ClaimCore.TestSupport

type private Step =
    {
        FileName: string
        Revision: int
        Command: string
        Status: string
        DecisionDate: string option
        PaymentDate: string option
    }

let private step fileName revision command status decisionDate paymentDate =
    {
        FileName = fileName
        Revision = revision
        Command = command
        Status = status
        DecisionDate = decisionDate
        PaymentDate = paymentDate
    }

let private steps =
    let decided = Some "2026-08-15"
    let paid = Some "2026-08-20"

    [
        step "01-open.json" 1 "OPEN" "OPENED" None None
        step "02-decide.json" 2 "DECIDE" "OPENED" decided None
        step "03-record-payment.json" 3 "RECORD_PAYMENT" "OPENED" decided paid
        step "04-close.json" 4 "CLOSE" "CLOSED" decided paid
        step "05-reopen.json" 5 "REOPEN" "OPENED" decided paid
        step "06-correct-payment-record.json" 6 "CLEAR_PAYMENT" "OPENED" decided None
    ]

let private reference = "DEMO-0001"

let private operationId revision =
    $"10000000-0000-4000-8000-{revision:D12}"

let private expectOptional (fields: JsonElement) (name: string) (expected: string option) =
    let actual = fields.GetProperty(name)

    match expected with
    | Some value -> Expect.isTrue (actual.GetString() = value) ("Expected " + name)
    | None -> Expect.equal actual.ValueKind JsonValueKind.Null ("Unrecorded " + name)

let private expectFinalFields (fields: JsonElement) =
    let expectedNames =
        set
            [
                "incidentDate"
                "incidentNotificationDate"
                "incidentCountry"
                "claimantName"
                "insurerName"
                "claimedAmount"
                "claimedCurrency"
                "caseReference"
                "paymentDecisionDate"
                "payableAmount"
                "payableCurrency"
                "paymentDate"
                "status"
            ]

    let actualNames = fields.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
    Expect.equal actualNames expectedNames "Exactly thirteen documented business fields"

    for name, value in
        [
            "incidentDate", "2026-08-01"
            "incidentNotificationDate", "2026-08-03"
            "incidentCountry", "Lithuania"
            "claimantName", "Example Translation Services Ltd"
            "insurerName", "Example Alleged Insurer"
            "claimedAmount", "1000"
            "claimedCurrency", "EUR"
            "caseReference", reference
            "paymentDecisionDate", "2026-08-15"
            "payableAmount", "750"
            "payableCurrency", "EUR"
            "status", "OPENED"
        ] do
        Expect.isTrue (fields.GetProperty(name).GetString() = value) ("Final " + name)

    expectOptional fields "paymentDate" None

let private expectCurrent context step =
    use response =
        CliV3Fixtures.caseGet reference
        |> CliV3Fixtures.call context
        |> CliV3Fixtures.decode 0 "case.get"

    let outcome = response.RootElement.GetProperty("outcome")
    Expect.equal (outcome.GetProperty("kind").GetString()) "found" "Example case found"
    let caseView = outcome.GetProperty("value").GetProperty("case")

    Expect.equal
        (caseView.GetProperty("revision").GetString())
        (string step.Revision)
        "Current revision"

    let fields = caseView.GetProperty("fields")
    Expect.equal (fields.GetProperty("status").GetString()) step.Status "Current status"
    expectOptional fields "paymentDecisionDate" step.DecisionDate
    expectOptional fields "paymentDate" step.PaymentDate

    if step.Revision = 6 then
        expectFinalFields fields

let private executeStep context step =
    let bytes =
        Path.Combine(RepositoryRoot.find (), "examples", step.FileName)
        |> File.ReadAllBytes

    use response =
        CliV3Fixtures.callBytes context bytes
        |> CliV3Fixtures.decode 0 "command.execute"

    let outcome = response.RootElement.GetProperty("outcome")
    Expect.equal (outcome.GetProperty("kind").GetString()) "completed" "Published example completed"
    let execution = outcome.GetProperty("execution")
    Expect.equal (execution.GetProperty("kind").GetString()) "accepted" "Example was accepted"
    let receipt = execution.GetProperty("receipt")

    Expect.equal
        (receipt.GetProperty("revision").GetString())
        (string step.Revision)
        "Accepted revision"

    Expect.equal (receipt.GetProperty("command").GetString()) step.Command "Accepted command"

    Expect.equal
        (receipt.GetProperty("operationId").GetString())
        (operationId step.Revision)
        "Exact example operation ID"

    Expect.equal
        (receipt.GetProperty("caseReference").GetString())
        reference
        "Exact example reference"

    Expect.isFalse (receipt.GetProperty("replayed").GetBoolean()) "First submission is new"
    expectCurrent context step
    bytes

let private expectHistory context =
    use response =
        CliV3Fixtures.caseHistory reference "SUMMARY"
        |> CliV3Fixtures.call context
        |> CliV3Fixtures.decode 0 "case.history"

    let outcome = response.RootElement.GetProperty("outcome")
    Expect.equal (outcome.GetProperty("kind").GetString()) "found" "Example history found"
    let entries = outcome.GetProperty("entries")
    Expect.equal (entries.GetArrayLength()) 6 "Exactly six accepted example entries"
    Expect.equal (outcome.GetProperty("nextCursor").ValueKind) JsonValueKind.Null "No more history"

    for step in steps do
        let entry = entries[step.Revision - 1]
        Expect.equal (entry.GetProperty("kind").GetString()) "summary" "History entry shape"

        Expect.equal
            (entry.GetProperty("revision").GetString())
            (string step.Revision)
            "Ordered history revision"

        Expect.equal
            (entry.GetProperty("command").GetString())
            step.Command
            "Ordered history command"

        Expect.equal
            (entry.GetProperty("operationId").GetString())
            (operationId step.Revision)
            "Ordered history operation"

let run context =
    let finalBytes = steps |> List.map (executeStep context) |> List.last
    expectHistory context

    use response =
        CliV3Fixtures.callBytes context finalBytes
        |> CliV3Fixtures.decode 0 "command.execute"

    let outcome = response.RootElement.GetProperty("outcome")

    Expect.equal
        (outcome.GetProperty("kind").GetString())
        "observedAccepted"
        "Exact replay observed"

    let receipt = outcome.GetProperty("receipt")
    Expect.equal (receipt.GetProperty("revision").GetString()) "6" "Replay retains sixth revision"

    Expect.equal
        (receipt.GetProperty("operationId").GetString())
        (operationId 6)
        "Replay retains operation ID"

    Expect.isTrue (receipt.GetProperty("replayed").GetBoolean()) "Replay returns retained receipt"
    expectHistory context
    expectCurrent context (List.last steps)
