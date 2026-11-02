module ClaimCore.Tests.WorkProtocolTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Json

let private accepted =
    function
    | Ok value -> value
    | Error issue -> failtestf "%A" issue

let private json value =
    StrictJson.read 32768 (Encoding.UTF8.GetBytes(value: string)) |> accepted

let private operation = "10000000-0000-4000-8000-000000000002"
let private parent = "10000000-0000-4000-8000-000000000001"
let private child = "10000000-0000-4000-8000-000000000003"

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
                                ("{\"kind\":\"UPDATE_WORK\",\"values\":{" + members + "}}"))
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
                        (input "CASE" "{\"kind\":\"UPDATE_WORK\",\"values\":{}}"))
                    "Retired financial work act refuses")
            testCase
                "reminder command structure excludes caller attribution and enforces the entity revision"
                (fun () ->
                    let target = "\"reminderId\":\"" + child + "\",\"expectedRevision\":3"

                    for act in
                        [
                            "{\"kind\":\"CREATE\",\"action\":\"Call insurer\",\"dueDate\":\"2026-11-10\"}"
                            "{\"kind\":\"UPDATE\","
                            + target
                            + ",\"action\":\"Call again\",\"dueDate\":\"2026-11-11\"}"
                            "{\"kind\":\"COMPLETE\"," + target + "}"
                            "{\"kind\":\"CANCEL\"," + target + "}"
                            "{\"kind\":\"REOPEN\"," + target + ",\"dueDate\":\"2026-11-11\"}"
                            "{\"kind\":\"COMPLETE_AND_PLAN_NEXT\","
                            + target
                            + ",\"action\":\"Review reply\",\"dueDate\":\"2026-11-11\"}"
                        ] do
                        Expect.isOk
                            (RequestCodecs.operationInput.Read "$" (input "REMINDER" act))
                            "Every declared operation has its distinct typed decoder"

                    for act in
                        [
                            "{\"kind\":\"COMPLETE\",\"reminderId\":\"" + child + "\"}"
                            "{\"kind\":\"COMPLETE\"," + target + ",\"recordedAt\":\"2026-11-01\"}"
                            "{\"kind\":\"CREATE\",\"action\":\"Call\",\"dueDate\":\"2026-11-10\",\"reminderId\":\""
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

                let operation = CatalogOperation.Act(ActKind.Work WorkActKind.UpdateWork)
                let inputs, _ = OperationCatalog.derive operation

                let summary =
                    inputs
                    |> List.find (fun value ->
                        value.Binding.Path = [ "act"; "values"; "handlingSummary" ])

                Expect.isTrue summary.Binding.AllowsNull "Explicit null clears"
                Expect.isFalse summary.Binding.Required "Omission keeps"
                Expect.isFalse summary.Binding.WireRequired "Not an invented required field")
        ]

[<Tests>]
let optionalSelector =
    testCase "optional reminder selector is absent until its reference is authored" (fun () ->
        let operation = CatalogOperation.Query QueryEndpoint.ReminderList
        let _, constants = OperationCatalog.derive operation

        let selector =
            constants |> List.find (fun value -> value.Path = [ "target"; "kind" ])

        Expect.isTrue selector.RequiresParent "Optional parent is a codec-owned condition"

        let prepared =
            ClaimCore.Cli.Grammar.prepare
                (fun () -> operation.ToString())
                [ "reminder"; "list"; "--limit"; "5" ]
            |> accepted

        match ClaimCore.Cli.Frames.value Api.reminders prepared.Invocation with
        | Some value -> Expect.isNone value.Target "Global queue does not invent a target"
        | None -> failtest "Expected queue request")

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
