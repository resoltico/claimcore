module ClaimCore.Tests.CaseStageTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.TestSupport
open ClaimCore.Tests.Fixtures

let private date text = CalendarDate.parse text |> accepted
let private openedOn = date "2026-08-02"
let private closedOn = date "2026-09-06"

let private describe state (_lastAct: CaseActKind option) =
    CaseStages.describe state (Some openedOn) (Some closedOn) |> accepted

let private tracker =
    function
    | CaseStageDisplay.Tracker(current, steps) -> current, steps
    | other -> failtestf "Expected tracker: %A" other

[<Tests>]
let tests =
    testList
        "core case stages"
        [
            testCase "closing without financial groups retains absent stages" (fun () ->
                let fields =
                    { viewFields (Case.snapshot (opened ())) with
                        Status = CaseStatus.Closed
                    }

                let current, steps =
                    describe (CaseState.Active fields) (Some CaseActKind.Close) |> tracker

                Expect.equal current CaseStageKind.Closed "Closed is the current step"

                Expect.equal
                    (steps |> List.map (fun s -> s.Stage, s.Completed, s.Date))
                    [
                        CaseStageKind.Opened, true, Some openedOn
                        CaseStageKind.Decided, false, None
                        CaseStageKind.Paid, false, None
                        CaseStageKind.Closed, true, Some closedOn
                    ]
                    "No invented decision or payment date/completion"

                Expect.equal steps[1].Amount None "An absent decision has no amount"
                Expect.equal steps[2].Amount None "An absent payment has no amount")
            testCase "financial steps use authored dates and current admitted money" (fun () ->
                let state = (Case.snapshot (paid ())).State
                let fields = viewFields (Case.snapshot (paid ()))
                let current, steps = describe state (Some CaseActKind.RecordPayment) |> tracker
                Expect.equal current CaseStageKind.Paid "Payment is current"

                Expect.equal
                    (steps |> List.map _.Completed)
                    [ true; true; true; false ]
                    "Only recorded groups complete"

                Expect.equal steps[0].Date (Some openedOn) "Opening uses ledger business date"

                Expect.equal
                    steps[1].Date
                    (fields.Decision |> Option.map Decision.date)
                    "Decision uses its authored date"

                Expect.equal
                    steps[2].Date
                    (fields.Payment |> Option.map Payment.date)
                    "Payment uses its authored date"

                Expect.equal
                    steps[2].Amount
                    (fields.Decision |> Option.map Decision.payable)
                    "Payment has its current payable amount"

                Expect.equal steps[3].Date None "An open case never shows a previous close date")
            testCase
                "voided and reinstated facts retain the tracker while erasure removes it"
                (fun () ->
                    let state = (Case.snapshot (opened ())).State
                    let current, steps = describe state (Some CaseActKind.Reinstate) |> tracker

                    Expect.equal
                        current
                        CaseStageKind.Opened
                        "Reinstatement does not replace financial progress"

                    Expect.equal
                        steps[0].Date
                        (Some openedOn)
                        "The retained opening date stays visible"

                    let current, _ =
                        describe (CaseState.Voided(viewFields (Case.snapshot (opened ())))) None
                        |> tracker

                    Expect.equal
                        current
                        CaseStageKind.Opened
                        "Voiding retains the factual tracker behind its notice"

                    Expect.equal
                        (describe CaseState.Erased None)
                        CaseStageDisplay.Erased
                        "Erasure has no factual tracker")
            testCase "missing retained completion dates fail rather than fabricate" (fun () ->
                let fields =
                    { viewFields (Case.snapshot (opened ())) with
                        Status = CaseStatus.Closed
                    }

                Expect.isError
                    (CaseStages.describe (CaseState.Active fields) (Some openedOn) None)
                    "Closed requires retained close metadata"

                Expect.isError
                    (CaseStages.describe (CaseState.Active fields) None (Some closedOn))
                    "Opened requires retained open metadata")
        ]
