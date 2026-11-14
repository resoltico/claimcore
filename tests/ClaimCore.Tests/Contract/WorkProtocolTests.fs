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

let private input task family selection values =
    json (
        sprintf
            """{"operationId":"%s","task":"%s","binding":{"kind":"%s","target":{"kind":"ID","caseId":"%s"}%s},"inputs":{%s}}"""
            operation
            task
            family
            parent
            selection
            values
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
                        "\"field:handlingSummary\":null", WorkValue.Clear
                        "\"field:handlingSummary\":\"  first\\nsecond  \"",
                        WorkValue.Set "  first\nsecond  "
                    ] do
                    let submission =
                        OperationSubmissionCodecs.submission.Read
                            "$"
                            (input "work.update" "WORK" ",\"expectedRevision\":0" members)
                        |> accepted

                    match submission.Operation |> accepted with
                    | SubmittedOperation.Command(OperationInput.Work {
                                                                         ExpectedRevision = 0L
                                                                         Act = WorkAct.UpdateWork value
                                                                     }) ->
                        Expect.equal value.Summary expected "Presence retains its meaning"

                        Expect.equal
                            value.Responsible
                            WorkValue.Keep
                            "Omitted owner stays untouched"
                    | other -> failtestf "%A" other

                Expect.isError
                    (OperationSubmissionCodecs.submission.Read
                        "$"
                        (input "work.update" "CASE" ",\"expectedRevision\":0" ""))
                    "Foreign subject binding refuses")
            testCase
                "reminder structure excludes caller attribution and requires the entity revision"
                (fun () ->
                    for task, values in
                        [
                            "reminder.create",
                            "\"input:reminderAction\":\"Call\",\"input:reminderDueDate\":\"2026-11-10\""
                            "reminder.update",
                            "\"input:reminderAction\":\"Call\",\"input:reminderDueDate\":\"2026-11-10\""
                            "reminder.complete", ""
                            "reminder.cancel", ""
                            "reminder.reopen", "\"input:reminderDueDate\":\"2026-11-10\""
                            "reminder.completeAndPlanNext",
                            "\"input:reminderAction\":\"Call\",\"input:reminderDueDate\":\"2026-11-10\""
                        ] do
                        let selection =
                            if task = "reminder.create" then
                                ""
                            else
                                $",\"reminderId\":\"{child}\",\"expectedRevision\":3"

                        let value = input task "REMINDER" selection values

                        Expect.isOk
                            (OperationSubmissionCodecs.submission.Read "$" value)
                            "Every task admits its declared input map"

                        let forged =
                            input
                                task
                                "REMINDER"
                                (selection + ",\"recordedAt\":\"2026-11-01\"")
                                values

                        Expect.isError
                            (OperationSubmissionCodecs.submission.Read "$" forged)
                            "Caller attribution cannot enter the binding"

                    for selection in
                        [
                            $",\"reminderId\":\"{child}\""
                            $",\"reminderId\":\"{child}\",\"expectedRevision\":0"
                        ] do
                        Expect.isError
                            (OperationSubmissionCodecs.submission.Read
                                "$"
                                (input "reminder.complete" "REMINDER" selection ""))
                            "Missing or zero revision refuses")
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
                    TaskInputMode.Update
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
