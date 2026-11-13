module ClaimCore.IntegrationTests.ReminderRestoreTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private parsed =
    function
    | Ok value -> value
    | Error issue -> failtestf "Expected isolated owner operation: %A" issue

let private target = CaseSelector.Reference "synthetic-a"
let private parent fixture = CaseFixtures.caseId (id fixture 10)

let private identicalOther fixture actor =
    task {
        let registration =
            Inputs.registrationWith
                [ BusinessField.IncidentDescription, "Restorable incident\nSecond paragraph" ]
                registration

        let! opened =
            Service.execute
                actor
                (Inputs.caseOperation (
                    request fixture 820 "synthetic-b" 0L (CaseAct.Open registration)
                ))
                token

        accepted opened |> ignore

        let work =
            OperationInput.Work
                {
                    OperationId = id fixture 821
                    Target = CaseSelector.Reference "synthetic-b"
                    ExpectedRevision = 0L
                    Act =
                        WorkAct.UpdateWork
                            {
                                Responsible =
                                    WorkValue.Set(
                                        ActorId.format (CaseFixtures.actorId (id fixture 1))
                                    )
                                Summary = WorkValue.Set "Restorable handling summary"
                            }
                }

        let! updated = Service.execute actor work token
        accepted updated |> ignore

        for operation, act in
            [
                822,
                AuthoredReminderAct.Create
                    {
                        Action = "Earlier action"
                        Due = "2027-01-01"
                    }
                823,
                AuthoredReminderAct.Update(
                    id fixture 822,
                    1L,
                    {
                        Action = "Current completed action"
                        Due = "2027-02-01"
                    }
                )
                824, AuthoredReminderAct.Complete(id fixture 822, 2L)
                825,
                AuthoredReminderAct.Create
                    {
                        Action = "Action to replace"
                        Due = "2027-03-01"
                    }
                826,
                AuthoredReminderAct.Create
                    {
                        Action = "Cancelled action"
                        Due = "2027-04-01"
                    }
                827, AuthoredReminderAct.Cancel(id fixture 826, 1L)
                828,
                AuthoredReminderAct.CompleteAndPlanNext(
                    id fixture 825,
                    1L,
                    {
                        Action = "Pending replacement"
                        Due = "2027-05-01"
                    }
                )
            ] do
            let! result =
                Service.execute
                    actor
                    (OperationInput.Reminder
                        {
                            OperationId = id fixture operation
                            Target = CaseSelector.Reference "synthetic-b"
                            Act = act
                        })
                    token

            accepted result |> ignore
    }

let private otherRows (database: PostgresFixture.Database) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        return!
            DatabaseSession.rows
                connection
                "SELECT 'root',row_to_json(l)::text FROM claimcore.ledger l WHERE EXISTS(SELECT 1 FROM claimcore.ledger_effects e WHERE e.operation_sequence=l.sequence AND ((e.subject_kind='CASE' AND e.subject_id=$1) OR e.parent_case_id=$1)) UNION ALL SELECT 'compartment',row_to_json(p)::text FROM claimcore.ledger_compartments p WHERE owner_kind='CASE' AND owner_id=$1 UNION ALL SELECT 'case',row_to_json(c)::text FROM claimcore.cases c WHERE case_id=$1 UNION ALL SELECT 'work',row_to_json(w)::text FROM claimcore.case_work w WHERE case_id=$1 UNION ALL SELECT 'reminder',row_to_json(r)::text FROM claimcore.reminders r WHERE case_id=$1 ORDER BY 1,2"
                [| box (id database.Fixture 820) |]
                token
    }

let private assertOther database expected =
    task {
        let! current = otherRows database

        Expect.equal
            current
            expected
            "Another case with identical narrative, summary, reminder texts/dates and assignee retains every current/historical byte and nonce"

        let! view =
            Service.get
                (core database.Fixture database.Fixture.Owner)
                {
                    Target = CaseSelector.Reference "synthetic-b"
                }
                token

        match view with
        | Outcome.Succeeded view ->
            let fields = CaseState.fields view.Snapshot.State |> Option.get

            Expect.equal
                (Registration.description fields.Registration
                 |> Option.map IncidentDescription.format)
                (Some "Restorable incident\nSecond paragraph")
                "The unrelated incident remains readable exactly"

            Expect.equal
                (view.Work.Summary |> Option.map HandlingSummary.format,
                 view.Work.Responsible |> Option.map _.ActorId)
                (Some "Restorable handling summary",
                 Some(CaseFixtures.actorId (id database.Fixture 1)))
                "Identical current context and inherited responsibility remain"
        | other -> failtestf "The unrelated readable case was affected: %A" other

        let! next =
            Service.reminder
                (core database.Fixture database.Fixture.Owner)
                {
                    Target = CaseSelector.Reference "synthetic-b"
                    ReminderId = id database.Fixture 828
                }
                token

        match next with
        | Outcome.Succeeded next ->
            Expect.equal
                (ReminderAction.format (Reminder.action next.Item.Reminder),
                 CalendarDate.format (Reminder.due next.Item.Reminder),
                 Reminder.state next.Item.Reminder)
                ("Pending replacement", "2027-05-01", ReminderState.Pending)
                "The unrelated compound replacement remains current"
        | other -> failtestf "The unrelated compound child was affected: %A" other
    }

let private seeded database =
    task {
        let! actor = populated database

        let patch =
            (RegistrationUpdateInput.replace
                [
                    BusinessField.IncidentDescription, Some("Restorable incident\nSecond paragraph")
                ]
                (Inputs.registrationUpdate registration)
             |> Result.defaultWith failtest)

        let! description =
            Service.execute
                actor
                (Inputs.caseOperation (
                    request database.Fixture 800 "synthetic-a" 1L (CaseAct.AmendRegistration patch)
                ))
                token

        accepted description |> ignore

        let! work =
            Service.execute
                actor
                (OperationInput.Work
                    {
                        OperationId = id database.Fixture 801
                        Target = target
                        ExpectedRevision = 0L
                        Act =
                            WorkAct.UpdateWork
                                {
                                    Responsible =
                                        WorkValue.Set(
                                            ActorId.format (
                                                CaseFixtures.actorId (id database.Fixture 1)
                                            )
                                        )
                                    Summary = WorkValue.Set "Restorable handling summary"
                                }
                    })
                token

        accepted work |> ignore

        for operation, act in
            [
                802,
                AuthoredReminderAct.Create
                    {
                        Action = "Earlier action"
                        Due = "2027-01-01"
                    }
                803,
                AuthoredReminderAct.Update(
                    id database.Fixture 802,
                    1L,
                    {
                        Action = "Current completed action"
                        Due = "2027-02-01"
                    }
                )
                804, AuthoredReminderAct.Complete(id database.Fixture 802, 2L)
                805,
                AuthoredReminderAct.Create
                    {
                        Action = "Action to replace"
                        Due = "2027-03-01"
                    }
                806,
                AuthoredReminderAct.Create
                    {
                        Action = "Cancelled action"
                        Due = "2027-04-01"
                    }
                807, AuthoredReminderAct.Cancel(id database.Fixture 806, 1L)
                808,
                AuthoredReminderAct.CompleteAndPlanNext(
                    id database.Fixture 805,
                    1L,
                    {
                        Action = "Pending replacement"
                        Due = "2027-05-01"
                    }
                )
            ] do
            let! result =
                Service.execute
                    actor
                    (OperationInput.Reminder
                        {
                            OperationId = id database.Fixture operation
                            Target = target
                            Act = act
                        })
                    token

            accepted result |> ignore

        let! permission =
            grant
                actor
                (id database.Fixture 809)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople permission
        do! identicalOther database.Fixture actor
        let! _ = verified database
        return actor
    }

let private mode (database: PostgresFixture.Database) change =
    task {
        let! result = RegisterMaintenanceStore.change database.OwnerSource change token
        return result
    }

let private erase fixture actor =
    task {
        let! erased =
            Service.execute
                actor
                (Inputs.caseOperation (
                    request fixture 810 "synthetic-a" 2L (CaseAct.Bare BareAct.Erase)
                ))
                token

        accepted erased |> ignore
    }

let private assertErased (database: PostgresFixture.Database) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! counts =
            DatabaseSession.rows
                connection
                "SELECT count(*) FROM claimcore.ledger_compartments WHERE owner_kind='CASE' AND owner_id=$1 AND (payload IS NOT NULL OR nonce IS NOT NULL) UNION ALL SELECT count(*) FROM claimcore.case_work WHERE case_id=$1 AND (snapshot IS NOT NULL OR responsible_actor IS NOT NULL OR handling_summary IS NOT NULL OR search_text IS NOT NULL OR summary_changed_at IS NOT NULL OR summary_recorded_by IS NOT NULL) UNION ALL SELECT count(*) FROM claimcore.reminders WHERE case_id=$1 AND (snapshot IS NOT NULL OR state IS NOT NULL OR action IS NOT NULL OR due IS NOT NULL OR search_text IS NOT NULL) UNION ALL SELECT count(*) FROM claimcore.cases WHERE case_id=$1 AND (snapshot IS NOT NULL OR search_text IS NOT NULL OR work_search_text IS NOT NULL OR responsible_actor IS NOT NULL OR follow_up IS NOT NULL)"
                [| box (CaseId.value (parent database.Fixture)) |]
                token

        Expect.equal
            (counts |> List.map (DatabaseSession.value<int64> 0))
            [ 0L; 0L; 0L; 0L ]
            "Actual related ledger bytes/nonces and every new payload projection are absent"

        let! identities =
            DatabaseSession.rows
                connection
                "SELECT count(*) FROM claimcore.reminders WHERE case_id=$1"
                [| box (CaseId.value (parent database.Fixture)) |]
                token

        Expect.equal
            (DatabaseSession.value<int64> 0 identities.Head)
            4L
            "Restoration/reconciliation never frees reserved reminder identities"
    }

let private dump database path =
    DatabaseScenarios.exec database [| "pg_dump"; "-U"; "postgres"; "-d"; "postgres"; "-f"; path |]

let private restore database path =
    task {
        do! sql database "DROP SCHEMA claimcore CASCADE" [||]
        do! sql database "DROP EXTENSION pg_trgm" [||]

        do!
            DatabaseScenarios.exec
                database
                [|
                    "psql"
                    "-U"
                    "postgres"
                    "-d"
                    "postgres"
                    "-v"
                    "ON_ERROR_STOP=1"
                    "-f"
                    path
                |]

        let! moduleRows =
            DatabaseSession.read database.OwnerSource token (fun connection ->
                DatabaseSession.rows
                    connection
                    "SELECT extversion FROM pg_extension WHERE extname='pg_trgm'"
                    [||]
                    token)

        Expect.equal
            (moduleRows |> List.map (DatabaseSession.value<string> 0))
            [ "1.6" ]
            "Each full restore recreates its module dependency"
    }

let private oldHead database =
    task {
        let! actor = seeded database
        let! other = otherRows database
        let! before = checkpoint database
        let! _ = mode database MaintenanceChange.On
        do! dump database "/tmp/reminders-before-erasure.sql"
        let! _ = mode database MaintenanceChange.Off
        do! erase database.Fixture actor
        do! assertOther database other
        let! acceptedErasure = checkpoint database

        Expect.notEqual
            acceptedErasure
            before
            "The retained external checkpoint covers the accepted erasure"

        let! _ = mode database MaintenanceChange.On
        do! dump database "/tmp/reminders-accepted-erasure.sql"
        do! restore database "/tmp/reminders-before-erasure.sql"

        let! restoredMode =
            DatabaseSession.read database.OwnerSource token (fun connection ->
                RegisterMaintenanceStore.read connection token)

        Expect.isTrue
            restoredMode.Active
            "The synthetic old backup was captured with register writes paused; traffic remains isolated"

        let! rejected = Verification.verify database.OwnerSource (Some acceptedErasure) token

        match rejected with
        | VerificationResult.Diverged finding ->
            Expect.equal
                finding.Finding
                VerificationFinding.ExpectedPosition
                "A whole old copy cannot prove a later accepted erasure that it does not contain"
        | other ->
            failtestf "An old head must not be admitted against the external checkpoint: %A" other
        // A retained later source is external evidence. Rebuild cannot invent missing accepted events.
        do! restore database "/tmp/reminders-accepted-erasure.sql"
        let! current = Verification.verify database.OwnerSource (Some acceptedErasure) token

        Expect.equal
            current
            (VerificationResult.Verified acceptedErasure.Position)
            "The isolated copy is checked against retained post-erasure evidence before any traffic admission"

        do! assertErased database
        do! assertOther database other
    }

let private mixedPayload database =
    task {
        let! actor = seeded database
        let! other = otherRows database
        let! _ = mode database MaintenanceChange.On
        use! connection = database.OwnerSource.OpenConnectionAsync()

        for statement in
            [
                "CREATE TEMP TABLE restore_compartments AS SELECT operation_sequence,ordinal,payload,nonce FROM claimcore.ledger_compartments WHERE owner_kind='CASE' AND owner_id=$1"
                "CREATE TEMP TABLE restore_cases AS SELECT * FROM claimcore.cases WHERE case_id=$1"
                "CREATE TEMP TABLE restore_work AS SELECT * FROM claimcore.case_work WHERE case_id=$1"
                "CREATE TEMP TABLE restore_reminders AS SELECT * FROM claimcore.reminders WHERE case_id=$1"
            ] do
            let! _ =
                DatabaseSession.execute
                    connection
                    statement
                    [| box (CaseId.value (parent database.Fixture)) |]
                    token

            ()

        let! _ = mode database MaintenanceChange.Off
        do! erase database.Fixture actor
        do! assertOther database other
        let! checkpoint = checkpoint database
        let! _ = mode database MaintenanceChange.On

        let restorePayload =
            "UPDATE claimcore.ledger_compartments p SET payload=b.payload,nonce=b.nonce FROM restore_compartments b WHERE p.operation_sequence=b.operation_sequence AND p.ordinal=b.ordinal"

        let mutable guardRefused = false

        try
            let! _ = DatabaseSession.execute connection restorePayload [||] token
            ()
        with :? Npgsql.PostgresException as error ->
            guardRefused <- error.SqlState = "CC003"

        Expect.isTrue
            guardRefused
            "The ordinary immutable ledger guard refuses restoration of erased bytes"
        // Deliberately simulate only a stale physical payload/projection copy in this owned fixture.
        // The accepted current head/ERASE metadata remain; this is distinct from oldHead above.
        use! transaction = connection.BeginTransactionAsync()

        let! _ =
            DatabaseSession.execute
                connection
                "SET LOCAL session_replication_role=replica"
                [||]
                token

        for statement in
            [
                restorePayload
                "UPDATE claimcore.cases c SET (case_reference,revision,state,snapshot,status,progress,changed_at,search_text,work_search_text,notified_on,responsible_actor,follow_up,claimed_amount,claimed_currency,payable_amount,payable_currency)=(b.case_reference,b.revision,b.state,b.snapshot,b.status,b.progress,b.changed_at,b.search_text,b.work_search_text,b.notified_on,b.responsible_actor,b.follow_up,b.claimed_amount,b.claimed_currency,b.payable_amount,b.payable_currency) FROM restore_cases b WHERE c.case_id=b.case_id"
                "UPDATE claimcore.case_work w SET (revision,snapshot,responsible_actor,handling_summary,search_text,changed_at,recorded_by,summary_changed_at,summary_recorded_by)=(b.revision,b.snapshot,b.responsible_actor,b.handling_summary,b.search_text,b.changed_at,b.recorded_by,b.summary_changed_at,b.summary_recorded_by) FROM restore_work b WHERE w.case_id=b.case_id"
                "UPDATE claimcore.reminders r SET (revision,snapshot,state,action,due,search_text,changed_at,recorded_by)=(b.revision,b.snapshot,b.state,b.action,b.due,b.search_text,b.changed_at,b.recorded_by) FROM restore_reminders b WHERE r.reminder_id=b.reminder_id"
            ] do
            let! _ = DatabaseSession.execute connection statement [||] token
            ()

        do! transaction.CommitAsync()
        do! finding VerificationFinding.ErasureIncomplete database

        do!
            sql
                database
                "SELECT claimcore.erase_case_payload($1)"
                [| box (CaseId.value (parent database.Fixture)) |]

        let! rebuilt = ProjectionRebuild.rebuild database.OwnerSource checkpoint token

        Expect.isOk
            rebuilt
            "The existing authorized erasure path and maintenance reconstruction reconcile all related subjects"

        let! current = verified database

        Expect.equal
            current
            checkpoint.Position
            "Payload reconciliation and projection rebuild do not change accepted ledger evidence"

        do! assertErased database
        do! assertOther database other
    }

[<Tests>]
let tests =
    testList
        "reminder restore isolation"
        [
            testCaseAsync
                "a whole pre-erasure backup is refused against the later external checkpoint"
                (run oldHead)
            testCaseAsync
                "accepted erasures reconcile restored related bytes before projection admission"
                (run mixedPayload)
        ]
