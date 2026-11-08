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
                    [ "ERASED"; "VOIDED"; "CLOSED"; "PAID"; "PARTLY_PAID"; "DECIDED"; "UNDECIDED" ]
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

let private reminder state due =
    Reminder.ofAccepted
        (ReminderId.create (Guid.Parse "bbc83e6f-ecff-4023-8ff5-3d45db933cc1")
         |> Option.get)
        (CaseId.create (Guid.Parse "b32ec722-dfb9-426a-b574-c813be19999e") |> Option.get)
        Revision.first
        (ReminderAction.parse "Contact claimant"
         |> Result.defaultWith (fun _ -> failtest "Valid action"))
        (date due)
        state
    |> Result.defaultWith (fun _ -> failtest "Valid accepted reminder")

[<Tests>]
let predicates =
    testList
        "core view predicates"
        [
            testCase
                "MINE includes assigned inactive cases while UNASSIGNED matches current attention"
                (fun () ->
                    for state in states do
                        Expect.isTrue
                            (CaseView.matches
                                today
                                actor
                                CaseView.Mine
                                (facts state (Some(actor, false)) None))
                            "Current assignment owns MINE regardless of lifecycle or availability"

                        let unassigned = facts state None None

                        Expect.equal
                            (CaseView.matches today actor CaseView.Unassigned unassigned)
                            (CaseAttention.at today unassigned
                             |> List.contains CaseAttention.Unassigned)
                            "Unassigned view and attention have identical boundaries"

                        Expect.isFalse
                            (CaseView.matches today actor CaseView.Mine unassigned)
                            "No assignment is never mine")
            testCase "case view progress, lifecycle and date predicates are independent" (fun () ->
                let opened progress due =
                    facts (CaseState.Active(CaseStatus.Opened, progress)) (Some(actor, true)) due

                for view, progress, due in
                    [
                        CaseView.Undecided, CaseProgress.Undecided, None
                        CaseView.DecidedUnpaid, CaseProgress.Decided, None
                        CaseView.Overdue, CaseProgress.Paid, Some(date "2026-11-03")
                        CaseView.DueToday, CaseProgress.Decided, Some(date "2026-11-04")
                        CaseView.NoNextAction, CaseProgress.Paid, None
                    ] do
                    Expect.isTrue
                        (CaseView.matches today actor view (opened progress due))
                        "Positive boundary"

                    Expect.isFalse
                        (CaseView.matches
                            today
                            actor
                            view
                            (facts (CaseState.Active(CaseStatus.Closed, progress)) None due))
                        "Closing excludes active work views"

                Expect.isFalse
                    (CaseView.matches
                        today
                        actor
                        CaseView.DecidedUnpaid
                        (opened CaseProgress.Paid None))
                    "Paid is not decided-unpaid"

                Expect.isFalse
                    (CaseView.matches
                        today
                        actor
                        CaseView.Overdue
                        (opened CaseProgress.Decided (Some(date "2026-11-04"))))
                    "Today is not overdue"

                for state in states do
                    let current = facts state None None

                    let closed =
                        match state with
                        | CaseState.Active(CaseStatus.Closed, _)
                        | CaseState.Voided(CaseStatus.Closed, _) -> true
                        | _ -> false

                    let voided =
                        match state with
                        | CaseState.Voided _ -> true
                        | _ -> false

                    Expect.equal
                        (CaseView.matches today actor CaseView.Closed current)
                        closed
                        "Closed includes readable retained closed status"

                    Expect.equal
                        (CaseView.matches today actor CaseView.Voided current)
                        voided
                        "Voided includes retained status"

                    Expect.isTrue
                        (CaseView.matches today actor CaseView.All current)
                        "ALL adds no restriction"

                    Expect.equal
                        (CaseView.matches today actor CaseView.NeedsAttention current)
                        (CaseAttention.at today current |> List.isEmpty |> not)
                        "Attention union contributes one case")
            testCase "actionable reminder views require pending and active open parent" (fun () ->
                for parentState in states do
                    for reminderState in
                        [ ReminderState.Pending; ReminderState.Completed; ReminderState.Cancelled ] do
                        let parent = facts parentState (Some(actor, true)) None
                        let current = reminder reminderState "2026-11-03"

                        let actionable =
                            match parentState with
                            | CaseState.Active(CaseStatus.Opened, _) ->
                                reminderState = ReminderState.Pending
                            | _ -> false

                        for view in
                            [ ReminderView.Pending; ReminderView.Mine; ReminderView.Overdue ] do
                            Expect.equal
                                (ReminderView.matches today actor view parent current)
                                actionable
                                "Queue predicates share the active-open pending guard"

                        for view in [ ReminderView.Today; ReminderView.Upcoming ] do
                            Expect.isFalse
                                (ReminderView.matches today actor view parent current)
                                "Overdue remains disjoint from today/upcoming"

                        Expect.equal
                            (ReminderView.matches today actor ReminderView.Completed parent current)
                            (parentState <> CaseState.Erased
                             && reminderState = ReminderState.Completed)
                            "Completed includes readable inactive parents"

                        Expect.equal
                            (ReminderView.matches today actor ReminderView.Cancelled parent current)
                            (parentState <> CaseState.Erased
                             && reminderState = ReminderState.Cancelled)
                            "Cancelled includes readable inactive parents")
            testCase
                "reminder MINE uses current responsibility and date equality is exact"
                (fun () ->
                    let parent =
                        facts
                            (CaseState.Active(CaseStatus.Opened, CaseProgress.Undecided))
                            None
                            None

                    Expect.isFalse
                        (ReminderView.matches
                            today
                            actor
                            ReminderView.Mine
                            parent
                            (reminder ReminderState.Pending "2026-11-04"))
                        "Unassigned work is not mine"

                    Expect.isTrue
                        (ReminderView.matches
                            today
                            actor
                            ReminderView.Today
                            parent
                            (reminder ReminderState.Pending "2026-11-04"))
                        "Today uses the snapshot business date"

                    Expect.isTrue
                        (ReminderView.matches
                            today
                            actor
                            ReminderView.Upcoming
                            parent
                            (reminder ReminderState.Pending "2026-11-05"))
                        "Tomorrow is upcoming")
            testCase "view survives admission alongside explicit filters" (fun () ->
                let authored =
                    { CaseListFilter.empty<string> with
                        View = Some CaseView.Mine
                        Lifecycles = [ Lifecycle.Active ]
                        Statuses = [ CaseStatus.Closed ]
                        Assignment = Some CaseAssignment.Unassigned
                    }

                let admitted =
                    CaseListFilter.admit authored
                    |> Result.defaultWith (fun _ -> failtest "Filter admits")

                Expect.equal admitted.View (Some CaseView.Mine) "Admission preserves view"

                Expect.equal
                    admitted.Statuses
                    [ CaseStatus.Closed ]
                    "View does not replace explicit status"

                Expect.equal
                    admitted.Assignment
                    (Some CaseAssignment.Unassigned)
                    "Contradictory intersections remain explicit"

                Expect.equal
                    (CaseListFilter.empty<string>).View
                    None
                    "Omitted view adds no restriction")
        ]
