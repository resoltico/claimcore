module ClaimCore.Tests.WorkProtocolTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json

let private accepted =
    function
    | Ok value -> value
    | Error issue -> failtestf "%A" issue

let private json value =
    StrictJson.read 32768 (Encoding.UTF8.GetBytes(value: string)) |> accepted

let private operation = "10000000-0000-7000-8000-000000000002"
let private parent = "10000000-0000-7000-8000-000000000001"
let private child = "10000000-0000-7000-8000-000000000003"

let private input family act =
    json (
        "{\"kind\":\""
        + family
        + "\",\"operationId\":\""
        + operation
        + "\",\"target\":{\"kind\":\"ID\",\"caseId\":\""
        + parent
        + "\"},"
        + (if family = "WORK" then "\"expectedRevision\":0," else "")
        + "\"act\":"
        + act
        + "}"
    )

[<Tests>]
let tests =
    testList
        "work protocol"
        [
            testCase "nullable summary clear differs from omission and supplied text" (fun () ->
                for members, expected in
                    [
                        "", WorkValue.Keep
                        "\"handlingSummary\":null", WorkValue.Clear
                        "\"handlingSummary\":\"  first\\nsecond  \"",
                        WorkValue.Set "  first\nsecond  "
                    ] do
                    match
                        RequestCodecs.operationInput.Read
                            "$"
                            (input
                                "WORK"
                                ("{\"kind\":\"work.update\",\"values\":{" + members + "}}"))
                        |> accepted
                    with
                    | OperationInput.Work {
                                              ExpectedRevision = 0L
                                              Act = WorkAct.UpdateWork value
                                          } ->
                        Expect.equal value.Summary expected "Explicit field presence is preserved"

                        Expect.equal
                            value.Responsible
                            WorkValue.Keep
                            "Omitted owner stays untouched"
                    | _ -> failtest "Expected independent work operation"

                Expect.isError
                    (RequestCodecs.operationInput.Read
                        "$"
                        (input "CASE" "{\"kind\":\"work.update\",\"values\":{}}"))
                    "Retired financial work act refuses")
            testCase
                "reminder command structure excludes caller attribution and enforces the entity revision"
                (fun () ->
                    let target = "\"reminderId\":\"" + child + "\",\"expectedRevision\":3"

                    for act in
                        [
                            "{\"kind\":\"reminder.create\",\"action\":\"Call insurer\",\"dueDate\":\"2026-11-10\"}"
                            "{\"kind\":\"reminder.update\","
                            + target
                            + ",\"action\":\"Call again\",\"dueDate\":\"2026-11-11\"}"
                            "{\"kind\":\"reminder.complete\"," + target + "}"
                            "{\"kind\":\"reminder.cancel\"," + target + "}"
                            "{\"kind\":\"reminder.reopen\","
                            + target
                            + ",\"dueDate\":\"2026-11-11\"}"
                            "{\"kind\":\"reminder.completeAndPlanNext\","
                            + target
                            + ",\"action\":\"Review reply\",\"dueDate\":\"2026-11-11\"}"
                        ] do
                        Expect.isOk
                            (RequestCodecs.operationInput.Read "$" (input "REMINDER" act))
                            "Every declared operation has its distinct typed decoder"

                    for act in
                        [
                            "{\"kind\":\"reminder.complete\",\"reminderId\":\"" + child + "\"}"
                            "{\"kind\":\"reminder.complete\","
                            + target
                            + ",\"recordedAt\":\"2026-11-01\"}"
                            "{\"kind\":\"reminder.create\",\"action\":\"Call\",\"dueDate\":\"2026-11-10\",\"reminderId\":\""
                            + child
                            + "\"}"
                        ] do
                        Expect.isError
                            (RequestCodecs.operationInput.Read "$" (input "REMINDER" act))
                            "Incomplete revision and forged creation/attribution refuse")
            testCase "narrative catalog communicates scalar count LF and null semantics" (fun () ->
                Expect.equal
                    (FieldPresentation.descriptor BusinessField.IncidentDescription)
                    (ScalarRule.PlainText
                        {
                            MaximumScalars = 600
                            MaximumUtf8Bytes = 4096
                            AllowsLineFeed = true
                        })
                    "Independent description expectations"

                Expect.equal
                    (FieldPresentation.descriptor BusinessField.HandlingSummary)
                    (ScalarRule.PlainText
                        {
                            MaximumScalars = 600
                            MaximumUtf8Bytes = 4096
                            AllowsLineFeed = true
                        })
                    "Independent summary expectations"

                let task =
                    TaskDeclarations.describe (
                        TaskId.Operation(ActKind.Work WorkActKind.UpdateWork)
                    )

                let summary =
                    task.Inputs
                    |> List.find (fun input ->
                        input.Input =
                            TaskInputId.Target(InputTarget.Field BusinessField.HandlingSummary))

                Expect.equal
                    summary.Mode
                    TaskInputMode.Optional
                    "Omission preserves; the wire boundary separately checks null clearing"

                Expect.equal
                    summary.Source
                    InputSource.Authored
                    "The interface acquires text, never a retained DTO")
        ]

[<Tests>]
let causalReceipts =
    testCase "bounded compound receipts reject structurally valid wrong causal effects" (fun () ->
        let path =
            System.IO.Path.Combine(
                ClaimCore.TestSupport.RepositoryRoot.find (),
                "tests/fixtures/reminder-receipt-controls.json"
            )

        use document = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText path)
        let cases = document.RootElement.GetProperty("cases").EnumerateArray() |> Seq.toList
        Expect.hasLength cases 4 "One positive and three intended corruption controls"

        for case in cases do
            let actual =
                Api.operationExecute.Output.Read
                    "$"
                    (json (case.GetProperty("value").GetRawText()))

            Expect.equal
                (Result.isOk actual)
                (case.GetProperty("valid").GetBoolean())
                (case.GetProperty("name").GetString() |> nonNull))
