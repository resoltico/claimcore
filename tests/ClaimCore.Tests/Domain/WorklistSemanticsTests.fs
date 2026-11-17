module ClaimCore.Tests.WorklistSemanticsTests

open System
open Expecto
open ClaimCore.Domain

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

[<Tests>]
let classificationPlans =
    testList
        "core work selection classifications"
        [
            testCase "case timing partitions absent dates and inactive retained states" (fun () ->
                for state in states do
                    for due, expected in
                        [
                            None, CaseDue.Unscheduled
                            Some(date "2026-11-03"), CaseDue.Overdue
                            Some(date "2026-11-04"), CaseDue.Today
                            Some(date "2026-11-05"), CaseDue.Upcoming
                        ] do
                        let current = facts state None due

                        let actionable =
                            match state with
                            | CaseState.Active(CaseStatus.Opened, _) -> true
                            | _ -> false

                        let expected = if actionable then expected else CaseDue.Inactive

                        let matching =
                            Vocabulary.all CaseDue.vocabulary
                            |> List.filter (fun value ->
                                WorkSelection.evaluate
                                    today
                                    (Some actor)
                                    current
                                    None
                                    (CaseDue.plan value))

                        Expect.equal
                            matching
                            [ expected ]
                            "Exactly the literal due classification matches"

                        Expect.equal
                            (CaseDue.at
                                today
                                (CaseState.lifecycle state)
                                (CaseState.fields state |> Option.map fst)
                                due)
                            expected
                            "The public classifier consumes the declared plan")
            testCase
                "assignment distinguishes retained unassigned from erased absence and unavailable mine"
                (fun () ->
                    for state in states do
                        for responsible in [ None; Some(actor, false) ] do
                            let current = facts state responsible None

                            Expect.equal
                                (WorkSelection.evaluate
                                    today
                                    (Some actor)
                                    current
                                    None
                                    (CaseAssignment.plan CaseAssignment.Mine))
                                responsible.IsSome
                                "Availability and terminal status do not hide mine"

                            Expect.equal
                                (WorkSelection.evaluate
                                    today
                                    (Some actor)
                                    current
                                    None
                                    (CaseAssignment.plan CaseAssignment.Unassigned))
                                (responsible.IsNone && state <> CaseState.Erased)
                                "Erased absence is not a retained assignment")
            testCase
                "explicit filters intersect while each supplied facet is an alternative"
                (fun () ->
                    let filter =
                        { CaseListFilter.empty<string> with
                            Lifecycles = [ Lifecycle.Active; Lifecycle.Voided ]
                            Statuses = [ CaseStatus.Closed ]
                            Progress = [ CaseProgress.Decided; CaseProgress.Paid ]
                            View = Some CaseView.Mine
                            Assignment = Some CaseAssignment.Mine
                            Due = Some CaseDue.Inactive
                            ResponsibleActor = Some actor
                        }

                    for state in states do
                        for responsible in [ None; Some(actor, false) ] do
                            let expected =
                                responsible.IsSome
                                && (match state with
                                    | CaseState.Active(CaseStatus.Closed,
                                                       (CaseProgress.Decided | CaseProgress.Paid))
                                    | CaseState.Voided(CaseStatus.Closed,
                                                       (CaseProgress.Decided | CaseProgress.Paid)) ->
                                        true
                                    | _ -> false)

                            Expect.equal
                                (WorkSelection.evaluate
                                    today
                                    (Some actor)
                                    (facts state responsible None)
                                    None
                                    (CaseListFilter.selection filter))
                                expected
                                "No supplied dimension replaces another"

                    let contradictory =
                        { filter with
                            Assignment = Some CaseAssignment.Unassigned
                        }

                    Expect.isFalse
                        (WorkSelection.evaluate
                            today
                            (Some actor)
                            (facts
                                (CaseState.Active(CaseStatus.Closed, CaseProgress.Paid))
                                (Some(actor, true))
                                None)
                            None
                            (CaseListFilter.selection contradictory))
                        "Mine and unassigned remain a real empty intersection")
            testCase
                "timing interpreter rejects unrelated predicates instead of inventing partial facts"
                (fun () ->
                    Expect.throws
                        (fun () ->
                            WorkSelection.timing
                                today
                                Lifecycle.Active
                                (Some CaseStatus.Opened)
                                None
                                (WorkSelection.Fact WorkPredicate.Mine)
                            |> ignore)
                        "A mismatched timing plan fails visibly")
        ]
