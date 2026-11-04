module ClaimCore.Tests.WorklistSemanticsTests

open System
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.Tests.PropertyHarness

let private states =
    [
        yield CaseState.Erased
        for status in [ CaseStatus.Opened; CaseStatus.Closed ] do
            for progress in [ CaseProgress.Undecided; CaseProgress.Decided; CaseProgress.Paid ] do
                yield CaseState.Active(status, progress)
                yield CaseState.Voided(status, progress)
    ]

[<Tests>]
let headline =
    testList
        "core case headline"
        [
            testCase "precedence is erased, voided, closed, paid, decided, undecided" (fun () ->
                for state, expected in
                    [
                        CaseState.Erased, CaseHeadline.Erased
                        CaseState.Voided(CaseStatus.Closed, CaseProgress.Paid), CaseHeadline.Voided
                        CaseState.Voided(CaseStatus.Opened, CaseProgress.Undecided),
                        CaseHeadline.Voided
                        CaseState.Active(CaseStatus.Closed, CaseProgress.Paid), CaseHeadline.Closed
                        CaseState.Active(CaseStatus.Closed, CaseProgress.Undecided),
                        CaseHeadline.Closed
                        CaseState.Active(CaseStatus.Opened, CaseProgress.Paid), CaseHeadline.Paid
                        CaseState.Active(CaseStatus.Opened, CaseProgress.Decided),
                        CaseHeadline.Decided
                        CaseState.Active(CaseStatus.Opened, CaseProgress.Undecided),
                        CaseHeadline.Undecided
                    ] do
                    Expect.equal
                        (CaseHeadline.ofState state)
                        expected
                        "State precedence belongs to the core")
            testCase "headline vocabulary is complete and stable" (fun () ->
                Expect.equal
                    (CaseHeadline.all |> List.map CaseHeadline.token)
                    [ "ERASED"; "VOIDED"; "CLOSED"; "PAID"; "DECIDED"; "UNDECIDED" ]
                    "The closed headline vocabulary has no renderer-specific additions")
        ]

[<Tests>]
let recommendation =
    testList
        "core recommended case act"
        [
            testCase "payment progress selects the preferred offered act" (fun () ->
                for progress, expected in
                    [
                        CaseProgress.Undecided, CaseActKind.Decide
                        CaseProgress.Decided, CaseActKind.RecordPayment
                        CaseProgress.Paid, CaseActKind.Close
                    ] do
                    Expect.equal
                        (CaseAdvice.recommended
                            (CaseState.Active(CaseStatus.Opened, progress))
                            CaseActKind.all)
                        (Some expected)
                        "The preferred act is independent of offer order")
            testCase "fallbacks are amend, then correct, never an arbitrary offered act" (fun () ->
                let state = CaseState.Active(CaseStatus.Opened, CaseProgress.Undecided)

                Expect.equal
                    (CaseAdvice.recommended
                        state
                        [ CaseActKind.Correct; CaseActKind.AmendRegistration ])
                    (Some CaseActKind.AmendRegistration)
                    "Amend takes precedence over correct"

                Expect.equal
                    (CaseAdvice.recommended state [ CaseActKind.Correct ])
                    (Some CaseActKind.Correct)
                    "Correct remains the last safe fallback"

                Expect.isNone
                    (CaseAdvice.recommended
                        state
                        [
                            CaseActKind.Void
                            CaseActKind.Reinstate
                            CaseActKind.Erase
                            CaseActKind.Open
                        ])
                    "No safe preferred or fallback act is offered")
            testCase "closed, voided and erased cases have no routine recommendation" (fun () ->
                for state in states do
                    match state with
                    | CaseState.Active(CaseStatus.Opened, _) -> ()
                    | _ ->
                        Expect.isNone
                            (CaseAdvice.recommended state CaseActKind.all)
                            "Inactive cases do not inherit active-work recommendations")
            testCase
                "random offer subsets always recommend an offered non-stewardship act"
                (fun () ->
                    run
                        "recommended case act safety"
                        (property {
                            let! state = Gen.item states

                            let! mask =
                                Gen.int32 (Range.constant 0 ((1 <<< CaseActKind.all.Length) - 1))

                            let offered =
                                CaseActKind.all
                                |> List.indexed
                                |> List.choose (fun (bit, act) ->
                                    if mask &&& (1 <<< bit) <> 0 then Some act else None)

                            let recommended = CaseAdvice.recommended state offered

                            return
                                recommended
                                |> Option.forall (fun act ->
                                    List.contains act offered
                                    && not (
                                        List.contains
                                            act
                                            [
                                                CaseActKind.Void
                                                CaseActKind.Reinstate
                                                CaseActKind.Erase
                                            ]
                                    ))
                                && recommended = CaseAdvice.recommended state (List.rev offered)
                        }))
        ]

let private actor =
    ActorId.create (Guid.Parse "df622ecf-677e-4eac-9085-f99a152a66cb") |> Option.get

let private today = DateOnly(2026, 11, 4)

let private date text =
    CalendarDate.parse text
    |> Result.defaultWith (fun _ -> failtest "Valid date fixture")

let private facts state responsible due : CaseWorkFacts =
    {
        State = state
        Responsible = responsible
        NextPendingDue = due
    }

[<Tests>]
let attention =
    testList
        "core case attention"
        [
            testCase
                "independent reasons form a distinct union including paid open cases"
                (fun () ->
                    let actual =
                        CaseAttention.at
                            today
                            (facts
                                (CaseState.Active(CaseStatus.Opened, CaseProgress.Paid))
                                None
                                None)

                    Expect.equal
                        actual
                        [
                            CaseAttention.NoNextAction
                            CaseAttention.Unassigned
                            CaseAttention.ReadyToClose
                        ]
                        "All applicable reasons survive"

                    Expect.equal
                        (List.distinct actual)
                        actual
                        "A case contributes each reason once")
            testCase "date boundaries and assignee availability retain their meanings" (fun () ->
                let state = CaseState.Active(CaseStatus.Opened, CaseProgress.Decided)

                for due, expected in
                    [
                        "2026-11-03", [ CaseAttention.Overdue ]
                        "2026-11-04", [ CaseAttention.DueToday ]
                        "2026-11-05", []
                    ] do
                    Expect.equal
                        (CaseAttention.at today (facts state (Some(actor, true)) (Some(date due))))
                        expected
                        "Timing uses the snapshot date"

                Expect.equal
                    (CaseAttention.at
                        today
                        (facts state (Some(actor, false)) (Some(date "2026-11-05"))))
                    [ CaseAttention.AssigneeUnavailable ]
                    "An unavailable assignee differs from unassigned")
            testCase "attention is absent for every inactive or closed state" (fun () ->
                for state in states do
                    match state with
                    | CaseState.Active(CaseStatus.Opened, _) -> ()
                    | _ ->
                        Expect.isEmpty
                            (CaseAttention.at today (facts state None None))
                            "Retained work is not current attention")
            testCase
                "all state, assignment and date combinations keep reasons unique and ready-to-close exact"
                (fun () ->
                    for state in states do
                        for responsible in [ None; Some(actor, true); Some(actor, false) ] do
                            for due in
                                [
                                    None
                                    Some(date "2026-11-03")
                                    Some(date "2026-11-04")
                                    Some(date "2026-11-05")
                                ] do
                                let result = CaseAttention.at today (facts state responsible due)
                                Expect.equal (List.distinct result) result "The union is distinct"

                                Expect.equal
                                    (List.contains CaseAttention.ReadyToClose result)
                                    (state = CaseState.Active(CaseStatus.Opened, CaseProgress.Paid))
                                    "Only active open paid cases are ready to close")
        ]

[<Tests>]
let views =
    testCase "core work view vocabularies are closed and ordered" (fun () ->
        Expect.equal
            (CaseView.all |> List.map CaseView.token)
            [
                "NEEDS_ATTENTION"
                "MINE"
                "OVERDUE"
                "DUE_TODAY"
                "NO_NEXT_ACTION"
                "UNASSIGNED"
                "UNDECIDED"
                "DECIDED_UNPAID"
                "CLOSED"
                "VOIDED"
                "ALL"
            ]
            "CASE views match the adopted vocabulary"

        Expect.equal
            (ReminderView.all |> List.map ReminderView.token)
            [ "PENDING"; "MINE"; "OVERDUE"; "TODAY"; "UPCOMING"; "COMPLETED"; "CANCELLED" ]
            "Reminder views remain a distinct vocabulary"

        Expect.equal
            (CaseAttention.all |> List.map CaseAttention.token)
            [
                "OVERDUE"
                "DUE_TODAY"
                "NO_NEXT_ACTION"
                "UNASSIGNED"
                "ASSIGNEE_UNAVAILABLE"
                "READY_TO_CLOSE"
            ]
            "Attention reasons are not views")
