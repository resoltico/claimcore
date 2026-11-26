module ClaimCore.IntegrationTests.LiveAcquisitionTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private token = CancellationToken.None

let private require =
    function
    | Ok value -> value
    | Error failure -> failtestf "Expected admitted fixture value: %A" failure

let private operation fixture number =
    id fixture number |> OperationId.create |> Option.get

let private inspect (database: PostgresFixture.Database) work =
    DatabaseSession.read database.RuntimeSource token (fun connection ->
        work (
            ReadTx(
                connection,
                database.Installation,
                database.Cipher,
                DateTimeOffset.UtcNow,
                token
            )
        ))

let private readCase database caseId =
    inspect database (fun tx -> FamilyReads.findCase tx caseId)

let private current database caseId =
    task {
        let! value = readCase database caseId in
        return value |> Option.defaultWith (fun () -> failtest "Current case required")
    }

let private acquire database caseId binding =
    task {
        let! value =
            inspect database (fun tx ->
                CaseFamilyAcquisition.familyForDecision tx caseId binding None)

        return
            value
            |> Option.defaultWith (fun () -> failtest "Selected family facts required")
    }

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected successful read: %A" other

let private bounded (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! service = caseWorker fixture

        let! steward =
            grant
                service
                (id fixture 3)
                fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople steward
        let caseId = CaseId.create (id fixture 10) |> Option.get
        let target = CaseSelector.Id caseId

        let execute number taskName binding inputs =
            task {
                let submission = Submissions.create taskName (id fixture number) binding inputs
                let! reply = Service.executeSubmission service submission token

                match reply.Outcome with
                | ExecuteResult.Accepted receipt -> return receipt
                | other ->
                    return failtestf "Expected accepted %s/%d, received %A" taskName number other
            }

        let! _ =
            execute
                10
                "case.open"
                (Binding.CaseNew Revision.zero)
                (Submissions.fields (RegistrationInput.values DomainInputs.registration)
                 |> Map.add
                     (Submissions.field BusinessField.CaseReference)
                     (SubmissionValue.Text "LIVE-FACTS"))

        let! initial = current database caseId

        let! _ =
            execute
                11
                "case.decide"
                (Binding.Case(target, initial.Snapshot.Revision))
                (Submissions.fields (DecisionInput.values DomainInputs.decision))

        let addNote number =
            execute
                number
                "note.add"
                (Binding.NoteNew target)
                (Map.ofList
                    [
                        Submissions.input InputTarget.NoteText,
                        SubmissionValue.Text "Synthetic unrelated note"
                    ])

        let addReminder number =
            execute
                number
                "reminder.create"
                (Binding.ReminderNew target)
                (Map.ofList
                    [
                        Submissions.input InputTarget.ReminderAction,
                        SubmissionValue.Text "Synthetic follow-up"
                        Submissions.input InputTarget.ReminderDueDate,
                        SubmissionValue.Text "2027-01-01"
                    ])

        let addPayment number =
            task {
                let! record = current database caseId

                return!
                    execute
                        number
                        "case.recordPayment"
                        (Binding.Case(target, record.Snapshot.Revision))
                        (Submissions.fields
                            [
                                BusinessField.PaymentDate, "2026-08-20"
                                BusinessField.PaidAmount, "1"
                                BusinessField.PaidCurrency, "EUR"
                            ])
            }

        for number in [ 20; 21 ] do
            let! _ = addNote number in ()

        for number in [ 100; 101 ] do
            let! _ = addPayment number in ()

        for number in [ 200; 201 ] do
            let! _ = addReminder number in ()

        let note = NoteId.ofOperationId (operation fixture 20)
        let payment = operation fixture 100
        let reminder = ReminderId.ofOperationId (operation fixture 200)

        let selections (record: StoredCase) =
            [
                Binding.Case(target, record.Snapshot.Revision)
                Binding.Note(target, note)
                Binding.CasePayment(target, record.Snapshot.Revision, payment)
                Binding.Reminder(target, reminder, Revision.first)
            ]

        let! before = current database caseId
        let mutable shapes = []

        for binding in selections before do
            let! acquired = acquire database caseId binding in shapes <- acquired.Shape :: shapes

        for index in 0..29 do
            let! _ = addNote (1000 + index)
            let! _ = addPayment (2000 + index)
            let! _ = addReminder (3000 + index)
            ()

        let! after = current database caseId
        let mutable grown = []

        for binding in selections after do
            let! acquired = acquire database caseId binding
            grown <- acquired.Shape :: grown

            Expect.isLessThanOrEqual
                acquired.Parts.Length
                3
                "Root/work plus the explicit selected part, independent of unrelated growth"

        Expect.equal
            grown
            shapes
            "Materialized rows, private roots and round trips stay fixed for the same selected scope"

        let! noteFacts = acquire database caseId (Binding.Note(target, note))
        Expect.equal noteFacts.Facts.Notes.Count 1 "Only the selected note header is acquired"

        Expect.isEmpty
            noteFacts.Facts.Payments
            "Financial totals do not materialize unrelated payment entries"

        Expect.isEmpty noteFacts.Facts.Reminders "Next due does not materialize unrelated reminders"

        Expect.equal
            (noteFacts.Facts.Case.Value.Content |> Option.get |> _.Finance.PaymentCount)
            32L
            "Accepted root owns complete payment facts"

        let! _ = verified database

        // Reject the replacement only after the existing entry's upsert and ledger append have run.
        do!
            sql
                database
                "CREATE FUNCTION claimcore.reject_new_payment_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NOT EXISTS(SELECT 1 FROM claimcore.case_payments WHERE payment_id=NEW.payment_id) THEN RAISE EXCEPTION 'synthetic late replacement failure' USING ERRCODE='CC009'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_new_payment_fixture BEFORE INSERT ON claimcore.case_payments FOR EACH ROW EXECUTE FUNCTION claimcore.reject_new_payment_fixture()"
                [||]

        let! basis = current database caseId

        let correction number amount =
            Submissions.create
                "case.correctPayment"
                (id fixture number)
                (Binding.CasePayment(target, basis.Snapshot.Revision, payment))
                (Submissions.fields
                    [
                        BusinessField.PaymentDate, "2026-08-21"
                        BusinessField.PaidAmount, amount
                        BusinessField.PaidCurrency, "EUR"
                    ])

        let first = correction 5000 "2"
        let second = correction 5001 "3"
        let! failed = Service.executeSubmission service first token

        match failed.Outcome with
        | ExecuteResult.Failed _ -> ()
        | other -> failtestf "Expected late transactional failure: %A" other

        do!
            sql
                database
                "DROP TRIGGER reject_new_payment_fixture ON claimcore.case_payments; DROP FUNCTION claimcore.reject_new_payment_fixture()"
                [||]

        let! rolledBack = current database caseId
        Expect.equal rolledBack basis "Late correction failure preserves the exact case projection"

        let! original =
            acquire database caseId (Binding.CasePayment(target, basis.Snapshot.Revision, payment))

        Expect.isFalse
            original.Facts.Payments[payment].Value.Removed
            "Late failure rolls back the earlier entry removal"

        let! outcomes =
            Task.WhenAll
                [|
                    Service.executeSubmission service first token
                    Service.executeSubmission service second token
                |]

        let winners =
            outcomes
            |> Array.choose (fun value ->
                match value.Outcome with
                | ExecuteResult.Accepted receipt -> Some receipt
                | _ -> None)

        let conflicts =
            outcomes
            |> Array.choose (fun value ->
                match value.Outcome with
                | ExecuteResult.Rejected(Refusal.RevisionConflict _) -> Some()
                | _ -> None)

        Expect.equal winners.Length 1 "Competing stale corrections cannot both be accepted"
        Expect.equal conflicts.Length 1 "The loser observes a real revision conflict"
        let winner = winners[0]

        let winningInput =
            if winner.OperationId = operation fixture 5000 then
                first
            else
                second

        let expectedReplacement =
            if winner.OperationId = operation fixture 5000 then
                2M
            else
                3M

        let disclosed =
            match winner.Subject with
            | ReceiptSubject.Case {
                                      Snapshot = SnapshotDisclosure.Disclosed value
                                  } -> value
            | _ -> failtest "Disclosed correction receipt required"

        Expect.equal
            (disclosed.Payments
             |> List.map (function
                 | PaymentDifference.Removed entry -> "REMOVED", PaymentEntry.id entry
                 | PaymentDifference.Recorded entry -> "RECORDED", PaymentEntry.id entry))
            [ "REMOVED", payment; "RECORDED", winner.OperationId ]
            "One receipt carries both ordered directions and identities"

        let! corrected = current database caseId
        let finance = CaseState.fields corrected.Snapshot.State |> Option.get |> _.Finance

        Expect.equal
            finance.PaymentCount
            32L
            "One active replacement, no intermediate or duplicate payment"

        Expect.equal
            (CaseFixtures.netPaid "EUR" finance |> Decimal.Parse)
            (31M + expectedReplacement)
            "Only selected money changed"

        let! repeated = Service.executeSubmission service winningInput token

        match repeated.Outcome with
        | ExecuteResult.AlreadyAccepted prior ->
            Expect.equal prior.OperationId winner.OperationId "Exact retry identity"
        | other -> failtestf "Expected AlreadyAccepted: %A" other

        let! lookup =
            Service.operationGet
                service
                {
                    OperationId = OperationId.value winner.OperationId
                }
                token

        match read lookup.Outcome with
        | OperationLookupResult.Accepted(receipt, true) ->
            Expect.equal
                receipt.Subject
                winner.Subject
                "Lookup retains the plural permitted receipt"
        | other -> failtestf "Expected accepted lookup: %A" other

        let! _ =
            execute
                5002
                "case.removePayment"
                (Binding.CasePayment(target, corrected.Snapshot.Revision, winner.OperationId))
                Map.empty

        let! remaining = current database caseId

        Expect.equal
            (CaseState.fields remaining.Snapshot.State
             |> Option.get
             |> _.Finance
             |> CaseFixtures.netPaid "EUR"
             |> Decimal.Parse)
            31M
            "Removing the replacement preserves every unrelated payment"

        let! _ =
            execute
                6000
                "reminder.update"
                (Binding.Reminder(target, reminder, Revision.first))
                (Map.ofList
                    [
                        Submissions.input InputTarget.ReminderAction,
                        SubmissionValue.Text "Synthetic follow-up"
                        Submissions.input InputTarget.ReminderDueDate,
                        SubmissionValue.Text "2026-12-01"
                    ])

        let! earlier = current database caseId

        Expect.equal
            earlier.Index.FollowUp
            (Some(CalendarDate.parse "2026-12-01" |> require))
            "Updating selected earliest due preserves the remaining extrema"

        let! _ =
            execute
                6001
                "reminder.complete"
                (Binding.Reminder(target, reminder, Revision.create 2L |> Option.get))
                Map.empty

        let! next = current database caseId

        Expect.equal
            next.Index.FollowUp
            (Some(CalendarDate.parse "2027-01-01" |> require))
            "Completing the earliest selected reminder uses the unmaterialized remainder"

        let! _ =
            execute
                6002
                "reminder.reopen"
                (Binding.Reminder(target, reminder, Revision.create 3L |> Option.get))
                (Map.ofList
                    [
                        Submissions.input InputTarget.ReminderDueDate,
                        SubmissionValue.Text "2026-12-01"
                    ])

        let! reopened = current database caseId

        Expect.equal
            reopened.Index.FollowUp
            (Some(CalendarDate.parse "2026-12-01" |> require))
            "Reopening restores the selected minimum"

        let! _ =
            execute
                6003
                "reminder.complete"
                (Binding.Reminder(target, reminder, Revision.create 4L |> Option.get))
                Map.empty

        let! _ = execute 6004 "note.remove" (Binding.Note(target, note)) Map.empty
        let! expected = current database caseId
        let! checkpoint = verified database
        do! enterMaintenance database

        let! rebuilt =
            ProjectionRebuild.rebuild
                database.OwnerSource
                database.MasterKey
                (InstallationCheckpoint.create fixture.InstallationId checkpoint None
                 |> Option.get)
                (MaintenanceHandle.create 1L (Guid.Parse "10000000-0000-4000-8000-000000000099")
                 |> Option.get)
                token

        Expect.isOk rebuilt "Complete independent replay reconstructs live projection deltas"
        let! reconstructed = current database caseId

        Expect.equal
            reconstructed
            expected
            "Replay/rebuild agrees on every case projection field and bytes"

        let! _ =
            RegisterMaintenanceStore.change
                database.OwnerSource
                database.MasterKey
                (MaintenanceChange.Resume(
                    MaintenanceHandle.create 1L (Guid.Parse "10000000-0000-4000-8000-000000000099")
                    |> Option.get
                ))
                token

        let! acquired =
            acquire database caseId (Binding.Case(target, expected.Snapshot.Revision))

        let stale =
            Binding.CaseErase(target, expected.Snapshot.Revision, acquired.Facts.Sequence)

        let! _ = addNote 7000

        let! refused =
            Service.executeSubmission
                service
                (Submissions.create "case.erase" (id fixture 7001) stale Map.empty)
                token

        Expect.equal
            refused.Outcome
            (ExecuteResult.Rejected(Refusal.Case CaseRefusal.FamilyChanged))
            "An unacquired child still fences a stale erasure review"

        let! fresh =
            acquire database caseId (Binding.Case(target, expected.Snapshot.Revision))

        let! _ =
            execute
                7002
                "case.erase"
                (Binding.CaseErase(target, expected.Snapshot.Revision, fresh.Facts.Sequence))
                Map.empty

        let! erased = current database caseId

        Expect.equal
            erased.Snapshot.State
            CaseState.Erased
            "Bulk erasure needs no per-child application materialization"

        let! _ = verified database

        let! privateRows =
            DatabaseSession.read database.OwnerSource token (fun connection ->
                DatabaseSession.rows
                    connection
                    "SELECT (SELECT count(*) FROM claimcore.case_payments WHERE case_id=$1 AND (payment_date IS NOT NULL OR paid_amount IS NOT NULL))+(SELECT count(*) FROM claimcore.reminders WHERE case_id=$1 AND snapshot IS NOT NULL)+(SELECT count(*) FROM claimcore.notes WHERE case_id=$1 AND text IS NOT NULL)+(SELECT count(*) FROM claimcore.ledger_compartments WHERE owner_kind='FAMILY' AND owner_id=$1 AND payload IS NOT NULL)"
                    [| box (CaseId.value caseId) |]
                    token)

        Expect.equal
            (DatabaseSession.value<int64> 0 privateRows.Head)
            0L
            "Every owned private projection and authoritative compartment is cleared"
    }

let private selectedCorruption (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! service = populated database
        let caseId = CaseId.create (id fixture 10) |> Option.get
        let target = CaseSelector.Id caseId

        let submission =
            Submissions.create
                "note.add"
                (id fixture 20)
                (Binding.NoteNew target)
                (Map.ofList
                    [
                        Submissions.input InputTarget.NoteText,
                        SubmissionValue.Text "Selected protected note"
                    ])

        let! reply = Service.executeSubmission service submission token
        accepted reply |> ignore
        let note = NoteId.ofOperationId (operation fixture 20)
        let! before = acquire database caseId (Binding.Note(target, note))
        Expect.equal before.Facts.Notes.Count 1 "Positive selected-root control"

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger_effects SET aggregate_id='10000000-0000-4000-8000-000000000099' WHERE part_kind='NOTE' AND part_id=$1"
                [| box (NoteId.value note) |]

        let! result =
            Service.executeSubmission
                service
                (Submissions.create
                    "note.remove"
                    (id fixture 21)
                    (Binding.Note(target, note))
                    Map.empty)
                token

        match result.Outcome with
        | ExecuteResult.Failed CoreFault.StoreIntegrityError -> ()
        | other ->
            failtestf "Selected/root ownership corruption must be an integrity failure: %A" other
    }

[<Tests>]
let tests =
    testList
        "bounded live acquisition"
        [
            testCaseAsync
                "selected roots stay bounded through unrelated growth, atomic correction, extrema, independent replay and erasure"
                (run bounded)
            testCaseAsync
                "a missing selected accepted owner anchor is not a plausible note refusal"
                (run selectedCorruption)
        ]
