module ClaimCore.IntegrationTests.ReminderViewQueryTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.WorklistFixtures

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Reminder view read failed: %A" other

let private execute pair input =
    task {
        let! pg = Service.execute pair.Postgres input token
        pair.Clock.ObservedAt <- (accepted pg).RecordedAt
        let! memory = Service.execute pair.Memory input token
        accepted memory |> ignore
    }

let private request view : ReminderListRequest =
    {
        View = view
        Target = None
        State = None
        Due = None
        Assignment = None
        Text = None
        AfterCursor = None
        Limit = 1
        IncludeInactive = None
    }

let private page pair request =
    task {
        let! pg = Service.reminders pair.Postgres request token
        let pg = read pg
        pair.Clock.ObservedAt <- pg.Basis.ObservedAt
        let! memory = Service.reminders pair.Memory request token

        Expect.equal
            (read memory)
            pg
            "Both stores apply view and explicit filters within the same read snapshot"

        return pg
    }

let private walk pair request expected =
    task {
        let mutable cursor = None
        let mutable remaining = expected
        let mutable finished = false

        while not finished do
            let! result = page pair { request with AfterCursor = cursor }
            let visible = remaining |> List.truncate request.Limit

            Expect.equal
                (result.Items
                 |> List.map (fun item -> Reminder.id item.Reminder |> ReminderId.value))
                visible
                "Independent reminder identities and order survive paging"

            remaining <- remaining |> List.skip visible.Length

            Expect.equal
                result.NextCursor.IsSome
                (not remaining.IsEmpty)
                "Continuation exists only for remaining matching work"

            cursor <- result.NextCursor
            finished <- cursor.IsNone
    }

let private qualify pair =
    task {
        let! context = Service.context pair.Postgres () token
        let context = read context

        for number, reference, offset, action in
            [
                6601, "work-01", -1, "Past action"
                6602, "work-01", 0, "Today action"
                6603, "work-01", 1, "Future action"
                6604, "mix-04", 0, "Completed action"
                6605, "Work-11", 0, "Cancelled action"
                6606, "mix-04", 0, "Retained pending"
            ] do
            do!
                execute
                    pair
                    (OperationInput.Reminder
                        {
                            OperationId = id number
                            Target = CaseSelector.Reference reference
                            Act =
                                AuthoredReminderAct.Create
                                    {
                                        Action = action
                                        Due =
                                            context.Runtime.EffectiveBusinessDate.AddDays(offset)
                                            |> CalendarDate.ofDate
                                            |> CalendarDate.format
                                    }
                        })

        do!
            execute
                pair
                (OperationInput.Work
                    {
                        OperationId = id 6607
                        Target = CaseSelector.Reference "work-01"
                        ExpectedRevision = 0L
                        Act =
                            WorkAct.UpdateWork
                                {
                                    Responsible = WorkValue.Set(ActorId.format context.ActorId)
                                    Summary = WorkValue.Keep
                                }
                    })

        do!
            execute
                pair
                (OperationInput.Reminder
                    {
                        OperationId = id 6608
                        Target = CaseSelector.Reference "mix-04"
                        Act = AuthoredReminderAct.Complete(id 6604, 1L)
                    })

        do!
            execute
                pair
                (Inputs.caseOperation (
                    StoreFixtures.request 6609 "mix-04" 3L (AuthoredCaseAct.Bare BareAct.Close)
                ))

        do!
            execute
                pair
                (OperationInput.Reminder
                    {
                        OperationId = id 6610
                        Target = CaseSelector.Reference "Work-11"
                        Act = AuthoredReminderAct.Cancel(id 6605, 1L)
                    })

        do!
            execute
                pair
                (Inputs.caseOperation (
                    StoreFixtures.request
                        6611
                        "Work-11"
                        2L
                        (AuthoredCaseAct.Void "Incorrect duplicate")
                ))

        for view, expected in
            [
                ReminderView.Pending, [ id 6601; id 6602; id 6603 ]
                ReminderView.Mine, [ id 6601; id 6602; id 6603 ]
                ReminderView.Overdue, [ id 6601 ]
                ReminderView.Today, [ id 6602 ]
                ReminderView.Upcoming, [ id 6603 ]
                ReminderView.Completed, [ id 6604 ]
                ReminderView.Cancelled, [ id 6605 ]
            ] do
            do! walk pair (request (Some view)) expected

        do! walk pair (request None) [ id 6601; id 6602; id 6603 ]

        do!
            walk
                pair
                { request None with
                    IncludeInactive = Some true
                }
                [ id 6601; id 6602; id 6606; id 6603 ]

        do!
            walk
                pair
                { request (Some ReminderView.Pending) with
                    IncludeInactive = Some true
                }
                [ id 6601; id 6602; id 6603 ]

        do!
            walk
                pair
                { request (Some ReminderView.Completed) with
                    IncludeInactive = Some false
                }
                []

        do!
            walk
                pair
                { request (Some ReminderView.Completed) with
                    State = Some ReminderState.Pending
                }
                []

        do!
            walk
                pair
                { request (Some ReminderView.Mine) with
                    Assignment = Some CaseAssignment.Unassigned
                }
                []

        do!
            walk
                pair
                { request (Some ReminderView.Upcoming) with
                    Due = Some ReminderDue.Today
                }
                []

        do!
            walk
                pair
                { request (Some ReminderView.Upcoming) with
                    Text = Some "Future"
                }
                [ id 6603 ]

        let! first = page pair (request (Some ReminderView.Pending))
        Expect.isSome first.NextCursor "The view continuation test has a real cursor"

        for changed in
            [
                request (Some ReminderView.Today)
                { request (Some ReminderView.Pending) with
                    State = Some ReminderState.Pending
                }
                { request (Some ReminderView.Pending) with
                    IncludeInactive = Some false
                }
            ] do
            for service in [ pair.Postgres; pair.Memory ] do
                let! result =
                    Service.reminders
                        service
                        { changed with
                            AfterCursor = first.NextCursor
                        }
                        token

                Expect.equal
                    result
                    (Outcome.Rejected Refusal.InvalidCursor)
                    "A continuation is bound to view and every explicit filter"
    }

[<Tests>]
let tests =
    testCaseAsync
        "reminder views preserve defaults, explicit intersections and cursor binding in both stores"
        (run qualify)
