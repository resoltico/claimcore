module ClaimCore.IntegrationTests.ViewCountQueryTests

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
    | other -> failtestf "Count read failed: %A" other

let private execute pair input =
    task {
        let! pg = Service.execute pair.Postgres input token
        pair.Clock.ObservedAt <- (accepted pg).RecordedAt
        let! memory = Service.execute pair.Memory input token
        accepted memory |> ignore
    }

let private cases pair expected =
    task {
        let! pg = Service.caseViews pair.Postgres () token
        let pg = read pg
        pair.Clock.ObservedAt <- pg.Basis.ObservedAt
        let! memory = Service.caseViews pair.Memory () token

        Expect.equal
            (read memory)
            pg
            "SQL and memory agree on the complete snapshot and all view counts"

        Expect.equal
            (pg.Items |> List.map (fun item -> item.View, item.Count))
            expected
            "Counts follow the independent fixture table, in declared order including zero counts"
    }

let private reminders pair expected =
    task {
        let! pg = Service.reminderViews pair.Postgres () token
        let pg = read pg
        pair.Clock.ObservedAt <- pg.Basis.ObservedAt
        let! memory = Service.reminderViews pair.Memory () token
        Expect.equal (read memory) pg "Both stores count reminders in the same authorized snapshot"

        Expect.equal
            (pg.Items |> List.map (fun item -> item.View, item.Count))
            expected
            "Reminder counts follow state, current parent and due boundaries"
    }

let private qualify pair =
    task {
        do!
            cases
                pair
                [
                    CaseView.NeedsAttention, 3L
                    CaseView.Mine, 0L
                    CaseView.Overdue, 0L
                    CaseView.DueToday, 0L
                    CaseView.NoNextAction, 3L
                    CaseView.Unassigned, 3L
                    CaseView.Undecided, 1L
                    CaseView.DecidedUnpaid, 1L
                    CaseView.Closed, 6L
                    CaseView.Voided, 6L
                    CaseView.All, 13L
                ]

        do!
            reminders
                pair
                [
                    ReminderView.Pending, 0L
                    ReminderView.Mine, 0L
                    ReminderView.Overdue, 0L
                    ReminderView.Today, 0L
                    ReminderView.Upcoming, 0L
                    ReminderView.Completed, 0L
                    ReminderView.Cancelled, 0L
                ]

        let! context = Service.context pair.Postgres () token

        let context =
            match (read context).Requester with
            | RequesterState.Active active -> active
            | state -> failtestf "Fixture requires an active requester: %A" state

        for operation, reference, offset in
            [ 5501, "work-01", 1; 5502, "mix-04", 0; 5503, "Work-11", -1 ] do
            do!
                execute
                    pair
                    (OperationInput.Reminder
                        {
                            OperationId = id operation
                            Target = CaseSelector.Reference reference
                            Act =
                                AuthoredReminderAct.Create
                                    {
                                        Action = "Check supplied evidence"
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
                        OperationId = id 5504
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
            cases
                pair
                [
                    CaseView.NeedsAttention, 3L
                    CaseView.Mine, 1L
                    CaseView.Overdue, 1L
                    CaseView.DueToday, 1L
                    CaseView.NoNextAction, 0L
                    CaseView.Unassigned, 2L
                    CaseView.Undecided, 1L
                    CaseView.DecidedUnpaid, 1L
                    CaseView.Closed, 6L
                    CaseView.Voided, 6L
                    CaseView.All, 13L
                ]
        // The paid case has an available assignee and an upcoming plan: READY_TO_CLOSE alone keeps it in attention.
        let! overview = Service.workOverview pair.Postgres { Mine = false } token
        let overview = read overview
        pair.Clock.ObservedAt <- overview.Basis.ObservedAt
        let! memoryOverview = Service.workOverview pair.Memory { Mine = false } token

        Expect.equal
            (read memoryOverview)
            overview
            "Overview shares the same snapshot semantics in both stores"

        Expect.equal
            overview.AttentionCases
            3L
            "The CLI overview includes ready-to-close attention just like case.views"

        let! mine = Service.workOverview pair.Postgres { Mine = true } token

        Expect.equal
            (read mine).AttentionCases
            1L
            "The assigned paid case needs attention despite an available person and future plan"

        do!
            reminders
                pair
                [
                    ReminderView.Pending, 3L
                    ReminderView.Mine, 1L
                    ReminderView.Overdue, 1L
                    ReminderView.Today, 1L
                    ReminderView.Upcoming, 1L
                    ReminderView.Completed, 0L
                    ReminderView.Cancelled, 0L
                ]

        do!
            execute
                pair
                (OperationInput.Work
                    {
                        OperationId = id 5520
                        Target = CaseSelector.Reference "mix-04"
                        ExpectedRevision = 0L
                        Act =
                            WorkAct.UpdateWork
                                {
                                    Responsible = WorkValue.Set(ActorId.format context.ActorId)
                                    Summary = WorkValue.Keep
                                }
                    })

        let! dueTodayMine = Service.workOverview pair.Postgres { Mine = true } token

        Expect.equal
            (read dueTodayMine).AttentionCases
            2L
            "A due-today-only case also contributes independently of overdue work"

        do!
            execute
                pair
                (OperationInput.Reminder
                    {
                        OperationId = id 5505
                        Target = CaseSelector.Reference "mix-04"
                        Act = AuthoredReminderAct.Complete(id 5502, 1L)
                    })

        do!
            execute
                pair
                (Inputs.caseOperation (
                    request 5506 "mix-04" 3L (AuthoredCaseAct.Bare BareAct.Close)
                ))

        do!
            execute
                pair
                (OperationInput.Reminder
                    {
                        OperationId = id 5507
                        Target = CaseSelector.Reference "Work-11"
                        Act = AuthoredReminderAct.Cancel(id 5503, 1L)
                    })

        do!
            execute
                pair
                (Inputs.caseOperation (
                    request 5508 "Work-11" 2L (AuthoredCaseAct.Void "Incorrect duplicate case")
                ))

        do!
            reminders
                pair
                [
                    ReminderView.Pending, 1L
                    ReminderView.Mine, 1L
                    ReminderView.Overdue, 0L
                    ReminderView.Today, 0L
                    ReminderView.Upcoming, 1L
                    ReminderView.Completed, 1L
                    ReminderView.Cancelled, 1L
                ]

        let scoped =
            PrincipalKey.human "https://issuer.example/" "view-count-scoped-reader"
            |> Result.defaultWith (fun _ -> failtest "Valid scoped principal")

        do!
            execute
                pair
                (OperationInput.Authority(
                    authorityRequest (id 5509) scoped (AuthorityAct.Register Inputs.actorProfile)
                ))

        let! paid =
            Service.get
                pair.Postgres
                {
                    Target = CaseSelector.Reference "work-01"
                }
                token

        let paid = read paid

        let grant =
            {
                Role = Role.CaseReader
                Scope = GrantTarget.CaseId paid.Snapshot.CaseId
            }

        do!
            execute
                pair
                (OperationInput.Authority(
                    authorityRequest (id 5510) scoped (AuthorityAct.Grant grant)
                ))

        let scopedPair =
            { pair with
                Postgres =
                    { pair.Postgres with
                        Principal = scoped
                    }
                Memory = { pair.Memory with Principal = scoped }
            }

        do!
            cases
                scopedPair
                [
                    CaseView.NeedsAttention, 1L
                    CaseView.Mine, 0L
                    CaseView.Overdue, 0L
                    CaseView.DueToday, 0L
                    CaseView.NoNextAction, 0L
                    CaseView.Unassigned, 0L
                    CaseView.Undecided, 0L
                    CaseView.DecidedUnpaid, 0L
                    CaseView.Closed, 0L
                    CaseView.Voided, 0L
                    CaseView.All, 1L
                ]

        do!
            reminders
                scopedPair
                [
                    ReminderView.Pending, 1L
                    ReminderView.Mine, 0L
                    ReminderView.Overdue, 0L
                    ReminderView.Today, 0L
                    ReminderView.Upcoming, 1L
                    ReminderView.Completed, 0L
                    ReminderView.Cancelled, 0L
                ]

        do!
            execute
                pair
                (OperationInput.Authority(
                    authorityRequest (id 5511) scoped (AuthorityAct.Revoke grant)
                ))

        for service in [ scopedPair.Postgres; scopedPair.Memory ] do
            let! denied = Service.caseViews service () token

            Expect.equal
                denied
                (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                "Revocation is enforced by the count read's current authorization snapshot"

            let! denied = Service.reminderViews service () token

            Expect.equal
                denied
                (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                "Revoked access never discloses reminder counts"
    }

[<Tests>]
let tests =
    testCaseAsync
        "view counts agree across SQL and Domain for all views and retained terminal work"
        (run qualify)
