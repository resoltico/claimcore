module ClaimCore.Tests.PaymentRowControlTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport
open ClaimCore.Tests.TaskBuilders

let private fullyPaid tasks =
    let state = decided () |> pay 3 "2026-02-02" "1000"
    let snapshot = CaseFamily.snapshot state |> Option.get

    let advice =
        CaseAdvice.create
            tasks
            None
            HistoryAccess.None
            ((Inputs.caseAdvice snapshot.State).Blocked
             |> List.filter (fun blocked -> not (List.contains blocked.Act tasks)))
        |> accepted

    { ResponseExamples.caseRead with
        Snapshot = snapshot
        Advice = advice
        Headline = CaseHeadline.PayableCovered
        Work = Inputs.workView snapshot.State
        Stages = DomainInputs.stageDisplay snapshot.State
        LatestPayments = [ state.Payments[operation 3].Value.Entry |> Option.get ]
        PaymentPayees = []
    }

[<Tests>]
let tests =
    testList
        "current payment row task composition"
        [
            for tasks in
                [
                    []
                    [ TaskId.ofLiteral "case.removePayment" ]
                    [ TaskId.ofLiteral "case.correctPayment" ]
                    [
                        TaskId.ofLiteral "case.removePayment"
                        TaskId.ofLiteral "case.correctPayment"
                    ]
                ] do
                testCase
                    (sprintf "fully paid current row preserves exact supplied tasks %A" tasks)
                    (fun () ->
                        let read = fullyPaid tasks
                        let document = HumanCaseDocument.ofCore read

                        let table =
                            document.Screen.Regions
                            |> List.pick (fun region ->
                                if region.Role = RegionRole.Payments then
                                    match region.Content with
                                    | RegionContent.Table table -> Some table
                                    | _ -> None
                                else
                                    None)

                        let row = table.Rows |> List.exactlyOne

                        Expect.equal
                            (row.Actions |> List.map _.Task)
                            tasks
                            "Shared binding shape never selects or replaces another task"

                        for action in row.Actions do
                            Expect.equal
                                action.Binding
                                (Some(
                                    Binding.CasePayment(
                                        CaseSelector.Id read.Snapshot.CaseId,
                                        read.Snapshot.Revision,
                                        operation 3
                                    )
                                ))
                                "Both explicit tasks bind the selected immutable payment and reviewed case version"

                            if action.Task = TaskId.ofLiteral "case.correctPayment" then
                                Expect.equal
                                    (Map.tryFind
                                        (TaskAffordances.field BusinessField.PaidCurrency)
                                        action.Seeds)
                                    (Some(InputValue.Text "EUR"))
                                    "Correction keeps the decision currency as the Core seed"

                                Expect.equal
                                    action.LabelId
                                    "task.case.correctPayment.label"
                                    "Row consumes the owner's exact task label"

                        Expect.isOk
                            (Codec.encode HumanDocumentCodecs.document document
                             |> Codec.decode
                                 Limits.MaximumResponseBytes
                                 HumanDocumentCodecs.document)
                            "Actual fully-paid producer controls survive the complete Wire boundary")
        ]
