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

let private execute fixture input =
    task {
        let! pg = Service.execute fixture.Service input token
        accepted pg |> ignore
    }

let private cases fixture expected =
    task {
        let! pg = Service.workViews fixture.Service () token
        let pg = read pg

        Expect.equal
            (pg.CaseViews |> List.map (fun item -> item.View, item.Count))
            expected
            "Counts follow the independent fixture table, in declared order including zero counts"
    }

let private reminders fixture expected =
    task {
        let! pg = Service.workViews fixture.Service () token
        let pg = read pg

        Expect.equal
            (pg.ReminderViews |> List.map (fun item -> item.View, item.Count))
            expected
            "Reminder counts follow state, current parent and due boundaries"
    }

let private qualify fixture =
    task {
        do!
            cases
                fixture
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
                fixture
                [
                    ReminderView.Pending, 0L
                    ReminderView.Mine, 0L
                    ReminderView.Overdue, 0L
                    ReminderView.Today, 0L
                    ReminderView.Upcoming, 0L
                    ReminderView.Completed, 0L
                    ReminderView.Cancelled, 0L
                ]

        let! context = Service.context fixture.Service () token

        let context =
            match (read context).Requester with
            | RequesterState.Active active -> active
            | state -> failtestf "Fixture requires an active requester: %A" state

        for operation, reference, offset in
            [ 5501, "work-01", 1; 5502, "mix-04", 0; 5503, "Work-11", -1 ] do
            do!
                execute
                    fixture
                    (OperationInput.Reminder
                        {
                            OperationId = id fixture.Database.Fixture operation
                            Target = CaseSelector.Reference reference
                            Act =
                                AuthoredReminderAct.Create
                                    {
                                        Action = "Check supplied evidence"
                                        Due =
                                            context.Runtime.Basis.BusinessDate.AddDays(offset)
                                            |> CalendarDate.ofDate
                                            |> CalendarDate.format
                                    }
                        })

        do!
            execute
                fixture
                (OperationInput.Work
                    {
                        OperationId = id fixture.Database.Fixture 5504
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
                fixture
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

        do!
            reminders
                fixture
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
                fixture
                (OperationInput.Work
                    {
                        OperationId = id fixture.Database.Fixture 5520
                        Target = CaseSelector.Reference "mix-04"
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
                fixture
                (OperationInput.Reminder
                    {
                        OperationId = id fixture.Database.Fixture 5505
                        Target = CaseSelector.Reference "mix-04"
                        Act = AuthoredReminderAct.Complete(id fixture.Database.Fixture 5502, 1L)
                    })

        do!
            execute
                fixture
                (Inputs.caseOperation (
                    request
                        fixture.Database.Fixture
                        5506
                        "mix-04"
                        3L
                        (AuthoredCaseAct.Bare BareAct.Close)
                ))

        do!
            execute
                fixture
                (OperationInput.Reminder
                    {
                        OperationId = id fixture.Database.Fixture 5507
                        Target = CaseSelector.Reference "Work-11"
                        Act = AuthoredReminderAct.Cancel(id fixture.Database.Fixture 5503, 1L)
                    })

        do!
            execute
                fixture
                (Inputs.caseOperation (
                    request
                        fixture.Database.Fixture
                        5508
                        "Work-11"
                        2L
                        (AuthoredCaseAct.Void "Incorrect duplicate case")
                ))

        do!
            reminders
                fixture
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
                fixture
                (OperationInput.People(
                    peopleRequest
                        (id fixture.Database.Fixture 5509)
                        scoped
                        (PeopleAct.Register Inputs.actorProfile)
                ))

        let! paid =
            Service.get
                fixture.Service
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
                fixture
                (OperationInput.People(
                    peopleRequest (id fixture.Database.Fixture 5510) scoped (PeopleAct.Grant grant)
                ))

        let scopedFixture =
            { fixture with
                Service =
                    { fixture.Service with
                        Principal = scoped
                    }
            }

        do!
            cases
                scopedFixture
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
                scopedFixture
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
                fixture
                (OperationInput.People(
                    peopleRequest (id fixture.Database.Fixture 5511) scoped (PeopleAct.Revoke grant)
                ))

        let service = scopedFixture.Service
        let! denied = Service.workViews service () token

        Expect.equal
            denied
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Revocation is enforced by the count read's current authorization snapshot"

        let! denied = Service.workViews service () token

        Expect.equal
            denied
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Revoked access never discloses reminder counts"
    }

[<Tests>]
let tests =
    testCaseAsync
        "view counts match literal expectations for all views and retained terminal work"
        (run qualify)
