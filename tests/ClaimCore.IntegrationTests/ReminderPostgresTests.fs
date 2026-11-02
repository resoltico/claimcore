module ClaimCore.IntegrationTests.ReminderPostgresTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected successful current read: %A" other

let private parsed =
    function
    | Ok value -> value
    | Error issue -> failtestf "Expected representable retained value: %A" issue

let private target = CaseSelector.Reference "synthetic-a"

let private reminder operation act =
    OperationInput.Reminder
        {
            OperationId = id operation
            Target = target
            Act = act
        }

let private queue actor cursor =
    Service.reminders
        actor
        {
            Target = Some target
            State = None
            Due = None
            Assignment = None
            Text = None
            AfterCursor = cursor
            Limit = 1
            IncludeInactive = false
        }
        token

let private dates database =
    task {
        let! actor = populated database

        for operation, date in [ 100, "0001-01-01"; 101, "9999-12-31" ] do
            let! created =
                Service.execute
                    actor
                    (reminder
                        operation
                        (AuthoredReminderAct.Create
                            {
                                Action = "Review the boundary date"
                                Due = date
                            }))
                    token

            accepted created |> ignore

        let! first = queue actor None
        let first = read first

        Expect.equal
            (first.Items
             |> List.map (fun row -> Reminder.due row.Reminder |> CalendarDate.format))
            [ "0001-01-01" ]
            "Minimum Gregorian date is stored as a date, not provider negative infinity"

        Expect.isSome first.NextCursor "The queue has another pending reminder"
        let! second = queue actor first.NextCursor
        let second = read second

        Expect.equal
            (second.Items
             |> List.map (fun row -> Reminder.due row.Reminder |> CalendarDate.format))
            [ "9999-12-31" ]
            "Maximum Gregorian date survives actual PostgreSQL and a continuation"

        Expect.isNone second.NextCursor "The actual queue has exactly two items"

        let! current = Service.get actor { Target = target } token
        let today = (read current).Work.Basis.BusinessDate

        let! dueToday =
            Service.execute
                actor
                (reminder
                    105
                    (AuthoredReminderAct.Create
                        {
                            Action = "Due on the observed business date"
                            Due = CalendarDate.format (CalendarDate.ofDate today)
                        }))
                token

        accepted dueToday |> ignore
        let! beforeFilters = verified database

        for filter in [ ReminderDue.Overdue; ReminderDue.Today; ReminderDue.Upcoming ] do
            let! result =
                Service.reminders
                    actor
                    {
                        Target = Some target
                        State = None
                        Due = Some filter
                        Assignment = None
                        Text = None
                        AfterCursor = None
                        Limit = 50
                        IncludeInactive = false
                    }
                    token

            let result = read result
            // Dates are independently authored; use this result's own observation if midnight passed.
            let expected =
                [ 100, System.DateOnly.MinValue; 105, today; 101, System.DateOnly.MaxValue ]
                |> List.filter (fun (_, date) ->
                    match filter with
                    | ReminderDue.Overdue -> date < result.Basis.BusinessDate
                    | ReminderDue.Today -> date = result.Basis.BusinessDate
                    | ReminderDue.Upcoming -> date > result.Basis.BusinessDate)
                |> List.map (fun (operation, _) ->
                    ReminderId.ofOperationId (CaseFixtures.operationId (id operation)))

            Expect.equal
                (result.Items |> List.map (fun item -> Reminder.id item.Reminder))
                expected
                "Each actual PostgreSQL reminder-date filter selects individual pending actions using its reported server date"

        let! nextDue = Service.get actor { Target = target } token

        Expect.equal
            (read nextDue).Work.Due
            CaseDue.Overdue
            "An overdue minimum keeps the case overdue even when the reminder queue selects an individual today action"

        let! todayCases =
            Service.list
                actor
                {
                    Page = { AfterCursor = None; Limit = 50 }
                    Filter =
                        { CaseListFilter.empty with
                            Due = Some CaseDue.Today
                        }
                }
                token

        Expect.isEmpty
            (read todayCases).Items
            "Case next-due filtering uses the minimum pending date, independently of the individual-reminder today filter"

        let! afterFilters = verified database

        Expect.equal
            afterFilters
            beforeFilters
            "Read-time due classification never appends synthetic ledger events"

        let! cancelledToday =
            Service.execute actor (reminder 106 (AuthoredReminderAct.Cancel(id 105, 1L))) token

        accepted cancelledToday |> ignore

        let! cancelled =
            Service.execute actor (reminder 102 (AuthoredReminderAct.Cancel(id 100, 1L))) token

        accepted cancelled |> ignore

        let minimumRegistration =
            Inputs.registrationWith
                [
                    BusinessField.IncidentDate, "0001-01-01"
                    BusinessField.IncidentNotificationDate, "0001-01-01"
                ]
                registration

        let! opened =
            Service.execute
                actor
                (Inputs.caseOperation (
                    request 103 "synthetic-b" 0L (CaseAct.Open minimumRegistration)
                ))
                token

        accepted opened |> ignore

        let! planned =
            Service.execute
                actor
                (OperationInput.Reminder
                    {
                        OperationId = id 104
                        Target = CaseSelector.Reference "synthetic-b"
                        Act =
                            AuthoredReminderAct.Create
                                {
                                    Action = "Another future action"
                                    Due = "9999-12-31"
                                }
                    })
                token

        accepted planned |> ignore

        let filter =
            { CaseListFilter.empty with
                Order = CaseListOrder.FollowUp
            }

        let! page =
            Service.list
                actor
                {
                    Page = { AfterCursor = None; Limit = 1 }
                    Filter = filter
                }
                token

        let page = read page
        Expect.isSome page.NextCursor "Equal maximum next dates need a reference tie continuation"

        let! continued =
            Service.list
                actor
                {
                    Page =
                        {
                            AfterCursor = page.NextCursor
                            Limit = 1
                        }
                    Filter = filter
                }
                token

        Expect.equal
            ((read continued).Items
             |> List.map (fun value -> CaseReference.format value.Reference))
            [ "synthetic-b" ]
            "The case continuation passes the exact maximum date without infinity conversion"

        let notified =
            { CaseListFilter.empty with
                Order = CaseListOrder.Notified
            }

        let! oldest =
            Service.list
                actor
                {
                    Page = { AfterCursor = None; Limit = 1 }
                    Filter = notified
                }
                token

        let oldest = read oldest

        Expect.equal
            (oldest.Items |> List.map (fun item -> CaseReference.format item.Reference))
            [ "synthetic-b" ]
            "A financial OPEN accepts the minimum Gregorian notification date and indexes it exactly"

        let firstDate =
            oldest.Items.Head.State
            |> CaseState.fields
            |> Option.map _.IncidentNotificationDate
            |> Option.map CalendarDate.format

        Expect.equal
            firstDate
            (Some "0001-01-01")
            "The financial list exposes the exact finite minimum date"

        Expect.isSome
            oldest.NextCursor
            "The minimum-date row has a later notification-date continuation"

        let! later =
            Service.list
                actor
                {
                    Page =
                        {
                            AfterCursor = oldest.NextCursor
                            Limit = 1
                        }
                    Filter = notified
                }
                token

        Expect.equal
            ((read later).Items |> List.map (fun item -> CaseReference.format item.Reference))
            [ "synthetic-a" ]
            "A notification cursor does not turn the finite minimum into provider negative infinity and repeat the first row"

        let! _ = verified database
        return ()
    }

let private atomicRollback database =
    task {
        let! actor = populated database

        let! initial =
            Service.execute
                actor
                (reminder
                    200
                    (AuthoredReminderAct.Create
                        {
                            Action = "Original action"
                            Due = "2027-01-01"
                        }))
                token

        accepted initial |> ignore
        let! before = verified database

        do!
            sql
                database
                (sprintf
                    "CREATE FUNCTION claimcore.inject_reminder_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.reminder_id='%O'::uuid THEN RAISE EXCEPTION 'intended synthetic second-effect failure'; END IF; RETURN NEW; END $$; CREATE TRIGGER inject_reminder_failure BEFORE INSERT ON claimcore.reminders FOR EACH ROW EXECUTE FUNCTION claimcore.inject_reminder_failure()"
                    (id 201))
                [||]

        let operation =
            reminder
                201
                (AuthoredReminderAct.CompleteAndPlanNext(
                    id 200,
                    1L,
                    {
                        Action = "Replacement action"
                        Due = "2027-01-02"
                    }
                ))

        let! failed = Service.execute actor operation token

        match failed.Outcome with
        | ExecuteResult.Failed _ -> ()
        | other -> failtestf "The actual second projection must fail: %A" other

        let! after = verified database

        Expect.equal
            after
            before
            "Actual PostgreSQL rolls back both ledger/head and the earlier completed projection"

        let! page = queue actor None
        let remaining = (read page).Items |> List.exactlyOne |> _.Reminder

        Expect.equal
            (Reminder.id remaining, Reminder.revision remaining, Reminder.state remaining)
            (ReminderId.ofOperationId (CaseFixtures.operationId (id 200)),
             Revision.first,
             ReminderState.Pending)
            "The original is still pending at revision one"

        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT count(*) FROM claimcore.ledger WHERE operation_id=$1 UNION ALL SELECT count(*) FROM claimcore.reminders WHERE reminder_id=$1"
                [| box (id 201) |]
                token

        Expect.equal
            (rows |> List.map (DatabaseSession.value<int64> 0))
            [ 0L; 0L ]
            "No partial accepted operation or reserved replacement was persisted"

        do!
            sql
                database
                "DROP TRIGGER inject_reminder_failure ON claimcore.reminders; DROP FUNCTION claimcore.inject_reminder_failure()"
                [||]

        let! acceptedPair = Service.execute actor operation token
        let receipt = accepted acceptedPair

        match receipt.Subject with
        | ReceiptSubject.Reminder {
                                      Effects = ReminderReceiptEffects.CompletedAndPlanned(completed,
                                                                                           next)
                                  } ->
            Expect.equal
                (completed.Revision, next.ReminderId, next.Revision)
                (CaseFixtures.revision 2L,
                 ReminderId.ofOperationId (CaseFixtures.operationId (id 201)),
                 Revision.first)
                "Positive control accepts one fixed correlated pair"
        | other -> failtestf "Expected compound receipt: %A" other

        let! replay = Service.execute actor operation token

        match replay.Outcome with
        | ExecuteResult.AlreadyAccepted replay ->
            Expect.equal replay receipt "Accepted retry is exactly the original pair"
        | other -> failtestf "Expected replay: %A" other

        let! _ = verified database
        return ()
    }

let private causalEffect database =
    task {
        let! actor = populated database

        let! planned =
            Service.execute
                actor
                (reminder
                    300
                    (AuthoredReminderAct.Create
                        {
                            Action = "Original action"
                            Due = "2027-01-01"
                        }))
                token

        accepted planned |> ignore

        let! completed =
            Service.execute actor (reminder 301 (AuthoredReminderAct.Complete(id 300, 1L))) token

        let receipt = accepted completed
        let! _ = verified database

        do!
            rewrite database receipt.Position.Sequence (fun original ->
                let data =
                    LedgerEntry.decodePayload
                        LedgerPayload.MaximumBytes
                        original.Entry.Payload.Value
                    |> parsed

                let value =
                    ReminderRecord.decodeEffects data.Snapshot
                    |> parsed
                    |> function
                        | ReminderEffects.Changed value -> value
                        | _ -> failtest "A single completion is required"

                let changed =
                    Reminder.ofAccepted
                        (Reminder.id value)
                        (Reminder.caseId value)
                        (Reminder.revision value)
                        (Reminder.action value)
                        (Inputs.date "2027-01-02")
                        ReminderState.Completed
                    |> parsed

                payload
                    data.Request
                    (ReminderRecord.encodeEffects (ReminderEffects.Changed changed))
                    original)

        do! finding VerificationFinding.StateSequence database
    }

let private causalParent database =
    task {
        let! actor = populated database

        let! other =
            Service.execute
                actor
                (Inputs.caseOperation (request 400 "synthetic-b" 0L (CaseAct.Open registration)))
                token

        let other = accepted other |> caseReceipt

        let! planned =
            Service.execute
                actor
                (reminder
                    401
                    (AuthoredReminderAct.Create
                        {
                            Action = "Original action"
                            Due = "2027-01-01"
                        }))
                token

        let receipt = accepted planned
        let! _ = verified database

        do!
            rewrite database receipt.Position.Sequence (fun original ->
                match original.Metadata.Subject with
                | RecordedTarget.Reminder subject ->
                    { original with
                        Metadata =
                            { original.Metadata with
                                Subject =
                                    RecordedTarget.Reminder { subject with CaseId = other.CaseId }
                            }
                    }
                | _ -> failtest "A reminder fixture is required")

        do! finding VerificationFinding.PayloadFormat database
    }

let private causalRevision database =
    task {
        let! actor = populated database

        let! planned =
            Service.execute
                actor
                (reminder
                    700
                    (AuthoredReminderAct.Create
                        {
                            Action = "Original action"
                            Due = "2027-01-01"
                        }))
                token

        accepted planned |> ignore

        let! completed =
            Service.execute actor (reminder 701 (AuthoredReminderAct.Complete(id 700, 1L))) token

        let receipt = accepted completed
        let! _ = verified database

        do!
            rewrite database receipt.Position.Sequence (fun original ->
                let data =
                    LedgerEntry.decodePayload
                        LedgerPayload.MaximumBytes
                        original.Entry.Payload.Value
                    |> parsed

                let request = ReminderRecord.decodeRequest data.Request |> parsed

                let changed =
                    match request.Command with
                    | ReminderCommand.Change(parent, id, _, ReminderChange.Complete) ->
                        { request with
                            Command =
                                ReminderCommand.Change(
                                    parent,
                                    id,
                                    Revision.create 9007199254740991L |> Option.get,
                                    ReminderChange.Complete
                                )
                        }
                    | _ -> failtest "A completion request fixture is required"

                payload (ReminderRecord.encodeRequest changed) data.Snapshot original)

        do! finding VerificationFinding.PayloadFormat database
    }

let private projectionRepair database =
    task {
        let! actor = populated database

        let! planned =
            Service.execute
                actor
                (reminder
                    500
                    (AuthoredReminderAct.Create
                        {
                            Action = "Original action"
                            Due = "2027-01-01"
                        }))
                token

        accepted planned |> ignore
        let! checkpoint = verified database
        do! sql database "UPDATE claimcore.reminders SET due=DATE '2027-01-02'" [||]
        do! finding VerificationFinding.CaseWorkProjection database
        do! enterMaintenance database
        let! repaired = ProjectionRebuild.rebuild database.OwnerSource checkpoint token
        Expect.isOk repaired "Rebuild reconstructs the original date from verified causal history"
        let! _ = verified database

        do! sql database "UPDATE claimcore.cases SET follow_up=DATE '2027-01-02'" [||]
        let! inconsistent = Service.get actor { Target = target } token

        Expect.equal
            inconsistent
            (Outcome.Failed CoreFault.StoreIntegrityError)
            "A point read checks derived scheduling against the independent reminder owner"

        do! finding VerificationFinding.CaseWorkProjection database
        let! repairedDate = ProjectionRebuild.rebuild database.OwnerSource checkpoint token
        Expect.isOk repairedDate "Rebuild reconciles the denormalized next-date projection"

        do!
            sql
                database
                "UPDATE claimcore.cases SET work_search_text='unrelated search payload'"
                [||]

        let! inconsistentSearch = Service.get actor { Target = target } token

        Expect.equal
            inconsistentSearch
            (Outcome.Failed CoreFault.StoreIntegrityError)
            "A point read checks the work search projection against the summary owner"

        do! finding VerificationFinding.CaseWorkProjection database
        let! repairedSearch = ProjectionRebuild.rebuild database.OwnerSource checkpoint token
        Expect.isOk repairedSearch "Independent work search columns are also part of reconstruction"
        let! _ = verified database
        return ()
    }

let private casePageOwners database =
    task {
        let! actor = populated database

        let! updated =
            Service.execute
                actor
                (OperationInput.Work
                    {
                        OperationId = id 550
                        Target = target
                        ExpectedRevision = 0L
                        Act =
                            WorkAct.UpdateWork
                                {
                                    Summary = WorkValue.Set "Page work summary"
                                    Responsible = WorkValue.Keep
                                }
                    })
                token

        accepted updated |> ignore

        let! planned =
            Service.execute
                actor
                (reminder
                    551
                    (AuthoredReminderAct.Create
                        {
                            Action = "Current page action"
                            Due = "2027-01-01"
                        }))
                token

        accepted planned |> ignore

        let request =
            {
                CaseListRequest.Page = { AfterCursor = None; Limit = 1 }
                Filter = CaseListFilter.empty
            }

        let check label =
            task {
                let! result = Service.list actor request token
                Expect.equal result (Outcome.Failed CoreFault.StoreIntegrityError) label
            }

        let! positive = Service.list actor request token

        let fields =
            (read positive).Items
            |> List.exactlyOne
            |> _.State
            |> CaseState.fields
            |> Option.get

        Expect.equal
            (fields.Work.Summary |> Option.map HandlingSummary.format)
            (Some "Page work summary")
            "The work owner is paired with its case"

        Expect.equal
            (fields.Work.PendingReminders,
             fields.Work.NextAction |> Option.map ReminderAction.format)
            (1L, Some "Current page action")
            "The page reads the independent reminder owner"

        let! checkpoint = verified database
        do! sql database "UPDATE claimcore.case_work SET search_text='wrong work owner'" [||]
        do! check "The joined work columns are checked against their own snapshot"

        do!
            sql
                database
                "UPDATE claimcore.case_work SET search_text='page work summary'; UPDATE claimcore.cases SET work_search_text='wrong case index'"
                [||]

        do!
            check
                "The joined work search cannot collide with the financial row's derived search column"

        do!
            sql
                database
                "UPDATE claimcore.cases SET work_search_text='page work summary'; DELETE FROM claimcore.case_work"
                [||]

        do! check "A missing work root refuses instead of dropping the selected parent"
        do! enterMaintenance database
        let! repaired = ProjectionRebuild.rebuild database.OwnerSource checkpoint token
        Expect.isOk repaired "Verified reconstruction restores the missing work owner"
        let! after = Service.list actor request token

        let restored =
            (read after).Items
            |> List.exactlyOne
            |> _.State
            |> CaseState.fields
            |> Option.get

        Expect.equal
            (restored.Work.Summary |> Option.map HandlingSummary.format)
            (Some "Page work summary")
            "The same replacement page path reads the repaired root"

        let! _ = verified database
        return ()
    }

let private currentLookup database =
    task {
        let! actor = populated database

        for operation, act in
            [
                600,
                AuthoredReminderAct.Create
                    {
                        Action = "Earlier action"
                        Due = "2027-01-01"
                    }
                601,
                AuthoredReminderAct.Update(
                    id 600,
                    1L,
                    {
                        Action = "Current action"
                        Due = "2027-02-01"
                    }
                )
                602, AuthoredReminderAct.Cancel(id 600, 2L)
            ] do
            let! result = Service.execute actor (reminder operation act) token
            accepted result |> ignore

        let reader =
            PrincipalKey.human "https://issuer.example/" "reminder-current-reader" |> parsed

        let! registered =
            Service.execute
                actor
                (OperationInput.Authority(authorityRequest (id 603) reader AuthorityAct.Register))
                token

        acceptedAuthority registered

        let! granted =
            grant
                actor
                (id 604)
                reader
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.CaseReference "synthetic-a"
                }
                true

        acceptedAuthority granted
        let services = core database.Fixture reader

        let! result =
            Service.reminder services { Target = target; ReminderId = id 600 } token

        let current = read result

        Expect.equal
            (Reminder.state current.Item.Reminder,
             ReminderAction.format (Reminder.action current.Item.Reminder),
             CalendarDate.format (Reminder.due current.Item.Reminder))
            (ReminderState.Cancelled, "Current action", "2027-02-01")
            "ReadCase discloses the current terminal record, not earlier text or dates"

        Expect.isEmpty
            current.Item.Acts
            "Current read access does not create work-management authority"

        Expect.equal
            current.Basis.TimeZoneId
            database.Fixture.Calendar.TimeZoneId
            "Singular reads carry the actual installation basis"

        let! history =
            Service.activity
                services
                {
                    Target = target
                    AfterCursor = None
                    Limit = 10
                    Detail = HistoryDetail.Full
                }
                token

        Expect.equal
            history
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "ReadCase does not disclose prior reminder versions"

        let! other =
            Service.execute
                actor
                (Inputs.caseOperation (request 605 "synthetic-b" 0L (CaseAct.Open registration)))
                token

        accepted other |> ignore

        let! planned =
            Service.execute
                actor
                (OperationInput.Reminder
                    {
                        OperationId = id 606
                        Target = CaseSelector.Reference "synthetic-b"
                        Act =
                            AuthoredReminderAct.Create
                                {
                                    Action = "Other case action"
                                    Due = "2027-01-01"
                                }
                    })
                token

        accepted planned |> ignore

        for requested in [ id 606; id 999 ] do
            let! hidden =
                Service.reminder
                    services
                    {
                        Target = target
                        ReminderId = requested
                    }
                    token

            Expect.equal
                hidden
                (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                "A foreign identity and an unknown identity have the same private result"

        let! malformed =
            Service.reminder
                services
                {
                    Target = target
                    ReminderId = Guid.Empty
                }
                token

        match malformed with
        | Outcome.Rejected(Refusal.InvalidInput issues) ->
            Expect.equal
                ((InputIssues.head issues).Target, (InputIssues.head issues).Violation)
                (InputTarget.ReminderId, Violation.ReminderIdRequired)
                "Malformed identity has a reminder-specific input diagnostic"
        | other -> failtestf "Expected malformed reminder identity input: %A" other
    }

[<Tests>]
let tests =
    testList
        "PostgreSQL reminder identity and effects"
        [
            testCaseAsync
                "case page joins preserve independent owner and missing-root integrity failures"
                (run casePageOwners)
            testCaseAsync
                "scoped current lookup includes terminal records without historical disclosure"
                (run currentLookup)
            testCaseAsync
                "Gregorian extremes survive current rows and both queue and case continuations"
                (run dates)
            testCaseAsync
                "injected second-effect failure rolls back the entire compound operation"
                (run atomicRollback)
            testCaseAsync
                "rehashed completion cannot silently change the retained due date"
                (run causalEffect)
            testCaseAsync
                "rehashed reminder parent must match its retained command"
                (run causalParent)
            testCaseAsync
                "maximum retained expected revision returns a finding without overflow"
                (run causalRevision)
            testCaseAsync
                "verification and rebuild cover reminder dates and independent work search"
                (run projectionRepair)
        ]
