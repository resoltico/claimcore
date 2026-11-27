module ClaimCore.IntegrationTests.ViewCountQueryTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.WorklistFixtures

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Count read failed: %A" other

let private execute fixture input =
    task {
        let! pg = Service.executeSubmission fixture.Service input token
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
                    ((ClaimCore.TestSupport.Submissions.createRaw)
                        "reminder.create"
                        (id fixture.Database.Fixture operation)
                        (ClaimCore.Application.Contracts.OperationBinding.Reminder(
                            ((caseTarget fixture.Database.Fixture reference)),
                            None
                        ))
                        (ClaimCore.TestSupport.Submissions.reminderPlan
                            ("Check supplied evidence")
                            (context.Runtime.Basis.BusinessDate.AddDays(offset)
                             |> CalendarDate.ofDate
                             |> CalendarDate.format)))

        do!
            execute
                fixture
                ((ClaimCore.TestSupport.Submissions.createRaw)
                    "work.update"
                    (id fixture.Database.Fixture 5504)
                    (ClaimCore.Application.Contracts.OperationBinding.Work(
                        ((caseTarget fixture.Database.Fixture "work-01")),
                        (0L)
                    ))
                    (ClaimCore.TestSupport.Submissions.work (
                        {
                            Responsible = WorkValue.Set(ActorId.format context.ActorId)
                            Summary = WorkValue.Keep
                        }
                    )))

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
                ((ClaimCore.TestSupport.Submissions.createRaw)
                    "work.update"
                    (id fixture.Database.Fixture 5520)
                    (ClaimCore.Application.Contracts.OperationBinding.Work(
                        ((caseTarget fixture.Database.Fixture "mix-04")),
                        (0L)
                    ))
                    (ClaimCore.TestSupport.Submissions.work (
                        {
                            Responsible = WorkValue.Set(ActorId.format context.ActorId)
                            Summary = WorkValue.Keep
                        }
                    )))

        do!
            execute
                fixture
                ((ClaimCore.TestSupport.Submissions.createRaw)
                    "reminder.complete"
                    (id fixture.Database.Fixture 5505)
                    (ClaimCore.Application.Contracts.OperationBinding.Reminder(
                        ((caseTarget fixture.Database.Fixture "mix-04")),
                        Some((id fixture.Database.Fixture 5502), (1L))
                    ))
                    (Map.empty))

        do!
            execute
                fixture
                (((request
                    (fixture.Database.Fixture)
                    (5506)
                    ("mix-04")
                    (3L)
                    "case.close"
                    (CaseTransitionInputs.closed))))

        do!
            execute
                fixture
                ((ClaimCore.TestSupport.Submissions.createRaw)
                    "reminder.cancel"
                    (id fixture.Database.Fixture 5507)
                    (ClaimCore.Application.Contracts.OperationBinding.Reminder(
                        ((caseTarget fixture.Database.Fixture "Work-11")),
                        Some((id fixture.Database.Fixture 5503), (1L))
                    ))
                    (Map.empty))

        do!
            execute
                fixture
                (((request
                    (fixture.Database.Fixture)
                    (5508)
                    ("Work-11")
                    (2L)
                    "case.void"
                    (Map.ofList
                        [
                            TaskInputId.Target InputTarget.Reason,
                            ClaimCore.Application.Contracts.SubmissionValue.Text(
                                "Incorrect duplicate case"
                            )
                        ]))))

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
                ((personSubmission
                    fixture.Database.Fixture.Store
                    (id fixture.Database.Fixture 5509)
                    scoped
                    None
                    "people.register"
                    (Submissions.profile (DomainInputs.actorProfile)))
                    .GetAwaiter()
                    .GetResult())

        let! paid =
            Service.get
                fixture.Service
                {
                    Target = (caseTarget fixture.Database.Fixture "work-01")
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
                ((personSubmission
                    fixture.Database.Fixture.Store
                    (id fixture.Database.Fixture 5510)
                    scoped
                    None
                    "people.grant"
                    (Submissions.grant (grant)))
                    .GetAwaiter()
                    .GetResult())

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
                ((personSubmission
                    fixture.Database.Fixture.Store
                    (id fixture.Database.Fixture 5511)
                    scoped
                    None
                    "people.revoke"
                    (Submissions.grant (grant)))
                    .GetAwaiter()
                    .GetResult())

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

let private translatorNegative fixture =
    task {
        let! context = Service.context fixture.Service () token

        let context =
            match (read context).Requester with
            | RequesterState.Active active -> active
            | other -> failtestf "Active fixture requester required: %A" other

        for operation, reference, offset in
            [ 5601, "work-01", -1; 5602, "mix-04", 0; 5603, "Work-11", 1 ] do
            do!
                execute
                    fixture
                    (Submissions.createRaw
                        "reminder.create"
                        (id fixture.Database.Fixture operation)
                        (OperationBinding.Reminder(
                            (caseTarget fixture.Database.Fixture reference),
                            None
                        ))
                        (Submissions.reminderPlan
                            "Check supplied evidence"
                            (context.Runtime.Basis.BusinessDate.AddDays(offset)
                             |> CalendarDate.ofDate
                             |> CalendarDate.format)))

        let selection =
            {
                CaseSelection.DateBasis = context.Runtime.Basis
                Visibility = CaseVisibility.Every
                ActorId = context.ActorId
                ReadRoles = Access.rolesFor Capability.ReadCase
                ForbiddenRoles = []
                Filter =
                    { CaseListFilter.empty<CaseSearchText> with
                        Due = Some CaseDue.Today
                    }
            }

        let predicate = CaseSelectionSql.predicate selection

        let wrong =
            predicate.Replace(
                "c.follow_up=$6::date",
                "c.follow_up<$6::date",
                System.StringComparison.Ordinal
            )

        Expect.notEqual wrong predicate "The negative changes the real date equality translation"
        use! connection = fixture.Database.RuntimeSource.OpenConnectionAsync(token)

        let query where =
            task {
                let! rows =
                    DatabaseSession.rows
                        connection
                        ($"SELECT c.case_id FROM claimcore.cases c WHERE {where} ORDER BY c.case_reference COLLATE \"C\"")
                        (CaseSelectionSql.parameters selection)
                        token

                return
                    rows
                    |> List.map (fun row ->
                        caseAlias
                            fixture.Database.Fixture
                            (CaseFixtures.caseId (unbox<System.Guid> row[0])))
            }

        let! actual = query predicate
        let! faulty = query wrong

        let witness values =
            Expect.equal values [ "mix-04" ] "Literal today witness excludes yesterday and tomorrow"

        witness actual

        Expect.equal
            faulty
            [ "work-01" ]
            "The mutated translator executes successfully and selects the known wrong boundary"

        Expect.throws
            (fun () -> witness faulty)
            "The same independent witness rejects a valid but incorrect SQL mapping"
    }

[<Tests>]
let mappingNegative =
    testCaseAsync
        "work selection date translator negative executes wrong boundary and literal witness rejects it"
        (run translatorNegative)
