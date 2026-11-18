module ClaimCore.Tests.CaseStageTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.TestSupport
open ClaimCore.Tests.CaseBuilders

let private date text = CalendarDate.parse text |> accepted
let private openedOn = date "2026-08-02"
let private closedOn = date "2026-09-06"

let private describe state =
    CaseStages.describe state (Some openedOn) (Some closedOn) None |> accepted

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
                    { viewFields ((CaseFamily.snapshot >> Option.get) (opened ())) with
                        Handling =
                            Handling.Closed
                                {
                                    Outcome = ClosureOutcome.Settled
                                    Note = None
                                }
                    }

                let current, steps = describe (CaseState.Active fields) |> tracker

                Expect.equal current CaseStageKind.Closed "Closed is the current step"

                Expect.equal
                    steps[3].Outcome
                    (Some ClosureOutcome.Settled)
                    "The tracker uses the recorded outcome"

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
                let state = ((CaseFamily.snapshot >> Option.get) (paid ())).State
                let fields = viewFields ((CaseFamily.snapshot >> Option.get) (paid ()))
                let paidOn = date "2026-08-20"

                let current, steps =
                    CaseStages.describe state (Some openedOn) None (Some paidOn)
                    |> accepted
                    |> tracker

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

                Expect.equal steps[2].Date (Some paidOn) "Payment uses its authored date"

                Expect.equal
                    (steps[2].NetPaid
                     |> Option.map (fun total -> DecimalTotal.format total.NetPaid))
                    (Some "750")
                    "Payment stage has the exact net in the decision currency"

                Expect.equal steps[3].Date None "An open case never shows a previous close date")
            testCase
                "voided and reinstated facts retain the tracker while erasure removes it"
                (fun () ->
                    let state = ((CaseFamily.snapshot >> Option.get) (opened ())).State
                    let current, steps = describe state |> tracker

                    Expect.equal
                        current
                        CaseStageKind.Opened
                        "Reinstatement does not replace financial progress"

                    Expect.equal
                        steps[0].Date
                        (Some openedOn)
                        "The retained opening date stays visible"

                    let current, _ =
                        describe (
                            CaseState.Voided(
                                viewFields ((CaseFamily.snapshot >> Option.get) (opened ()))
                            )
                        )
                        |> tracker

                    Expect.equal
                        current
                        CaseStageKind.Opened
                        "Voiding retains the factual tracker behind its notice"

                    Expect.equal
                        (describe CaseState.Erased)
                        CaseStageDisplay.Erased
                        "Erasure has no factual tracker")
            testCase "closed storage requires an outcome token" (fun () ->
                let decode (text: string) =
                    ClaimCore.Json.Codec.decode
                        2048
                        ClaimCore.RecordFormat.HandlingCodecs.value
                        (System.Text.Encoding.UTF8.GetBytes text)

                Expect.isError
                    (decode """{"kind":"CLOSED"}""")
                    "Missing outcome is structurally invalid"

                Expect.isOk
                    (decode """{"kind":"CLOSED","outcome":"DECLINED"}""")
                    "Authored outcome control")
            testCase "missing retained completion dates fail rather than fabricate" (fun () ->
                let fields =
                    { viewFields ((CaseFamily.snapshot >> Option.get) (opened ())) with
                        Handling =
                            Handling.Closed
                                {
                                    Outcome = ClosureOutcome.Settled
                                    Note = None
                                }
                    }

                Expect.isError
                    (CaseStages.describe (CaseState.Active fields) (Some openedOn) None None)
                    "Closed requires retained close metadata"

                Expect.isError
                    (CaseStages.describe (CaseState.Active fields) None (Some closedOn) None)
                    "Opened requires retained open metadata")
        ]
