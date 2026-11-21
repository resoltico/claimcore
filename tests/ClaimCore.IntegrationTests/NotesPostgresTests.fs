module ClaimCore.IntegrationTests.NotesPostgresTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private target = CaseSelector.Reference "synthetic-a"

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected read: %A" other

let private note fixture number taskName selected values =
    Submissions.createRaw
        taskName
        (id fixture number)
        (OperationBinding.Note(target, selected))
        values

let private notes service cursor limit =
    Service.notes
        service
        {
            Target = target
            AfterCursor = cursor
            Limit = limit
        }
        token

let private executeCase (database: PostgresFixture.Database) service number revision task values =
    Service.executeSubmission
        service
        (((request database.Fixture number "synthetic-a" revision task values)))
        token

let private lifecycle database =
    task {
        let! owner = populated database

        let readerPrincipal =
            PrincipalKey.human "https://issuer.example/" "note-reader"
            |> Result.defaultWith (string >> invalidOp)

        let! registered =
            (task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (database.Fixture.Store)
                        ((id database.Fixture 100))
                        (readerPrincipal)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (DomainInputs.actorProfile))

                return! Service.executeSubmission (owner) submission (token)
            })

        acceptedPeople registered

        let! granted =
            grant
                owner
                (id database.Fixture 101)
                readerPrincipal
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople granted
        let reader = core database.Fixture readerPrincipal

        let! steward =
            grant
                owner
                (id database.Fixture 102)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople steward

        let! auditor =
            grant
                owner
                (id database.Fixture 103)
                database.Fixture.Owner
                {
                    Role = Role.Auditor
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople auditor

        for number in [ 110..113 ] do
            let! acceptedNote =
                Service.executeSubmission
                    owner
                    (note
                        (database.Fixture)
                        (number)
                        "note.add"
                        (None)
                        (Map.ofList
                            [
                                TaskInputId.Target InputTarget.NoteText,
                                ClaimCore.Application.Contracts.SubmissionValue.Text(
                                    sprintf "Note %d\nSecond line" number
                                )
                            ]))
                    token

            accepted acceptedNote |> ignore

        let! current = Service.get reader { Target = target } token
        let current = read current
        Expect.equal current.Snapshot.Revision Revision.first "Notes do not consume case revisions"
        Expect.equal current.Advice.History HistoryAccess.Summary "Reader has no full history"
        Expect.equal current.Notes.Count 4L "Current count"

        Expect.equal
            (current.Notes.Items |> List.map (fun item -> NoteId.value item.NoteId))
            [ id database.Fixture 113; id database.Fixture 112; id database.Fixture 111 ]
            "Latest three descending"

        Expect.isFalse current.Notes.CanAdd "Read permission never offers writes"

        Expect.isTrue
            (current.Notes.Items |> List.forall (fun note -> not note.CanRemove))
            "No remove permission"

        let! first = notes reader None 2
        let first = read first
        Expect.equal first.Notes.Items.Length 2 "Bounded page"
        Expect.isSome first.NextCursor "Continuation"
        let! second = notes reader first.NextCursor 2

        Expect.equal
            ((read second).Notes.Items |> List.map (fun note -> NoteId.value note.NoteId))
            [ id database.Fixture 111; id database.Fixture 110 ]
            "Remaining page"

        let! tooMany = notes reader None 21

        Expect.isTrue
            (match tooMany with
             | Outcome.Rejected(Refusal.InvalidInput _) -> true
             | _ -> false)
            "Maximum 20"

        let! removed =
            Service.executeSubmission
                owner
                (note
                    (database.Fixture)
                    (120)
                    "note.remove"
                    (Some((id database.Fixture 111)))
                    (Map.empty))
                token

        accepted removed |> ignore
        let! after = notes reader None 20
        Expect.equal (read after).Notes.Count 3L "Removed note absent from current read"

        Expect.isFalse
            ((read after).Notes.Items
             |> List.exists (fun note -> NoteId.value note.NoteId = id database.Fixture 111))
            "Removed text is gone"

        let! readerReceipt =
            Service.operationGet
                reader
                {
                    OperationId = id database.Fixture 111
                }
                token

        Expect.isTrue
            (match read readerReceipt.Outcome with
             | OperationLookupResult.Accepted({ Subject = ReceiptSubject.Note value }, _) ->
                 value.Snapshot = NoteDisclosure.Withheld
             | _ -> false)
            "Removed text is withheld from historical receipts without FULL access"

        let! currentReceipt =
            Service.operationGet
                reader
                {
                    OperationId = id database.Fixture 110
                }
                token

        Expect.isTrue
            (match read currentReceipt.Outcome with
             | OperationLookupResult.Accepted({
                                                  Subject = ReceiptSubject.Note {
                                                                                    Snapshot = NoteDisclosure.Disclosed value
                                                                                }
                                              },
                                              _) -> (Note.text value).IsSome
             | _ -> false)
            "ReadCase still discloses a current accepted note"

        let! fullReceipt =
            Service.operationGet
                owner
                {
                    OperationId = id database.Fixture 111
                }
                token

        Expect.isTrue
            (match read fullReceipt.Outcome with
             | OperationLookupResult.Accepted({
                                                  Subject = ReceiptSubject.Note {
                                                                                    Snapshot = NoteDisclosure.Disclosed value
                                                                                }
                                              },
                                              _) ->
                 Note.text value
                 |> Option.exists (fun text -> NoteText.format text = "Note 111\nSecond line")
             | _ -> false)
            "FULL history discloses the removed accepted text"

        let! unavailable =
            Service.executeSubmission
                owner
                (note
                    (database.Fixture)
                    (121)
                    "note.remove"
                    (Some((id database.Fixture 111)))
                    (Map.empty))
                token

        Expect.isTrue
            (match unavailable.Outcome with
             | ExecuteResult.Rejected _ -> true
             | _ -> false)
            "Remove is not offered twice"

        let! hiddenHistory =
            Service.timeline
                reader
                {
                    Target = target
                    Filter = Some TimelineFilter.Notes
                    AfterCursor = None
                    Limit = 20
                }
                token

        Expect.isTrue
            ((read hiddenHistory).Items
             |> List.forall (fun entry -> entry.Changes = TimelineChanges.Withheld))
            "Summary history withholds note text"

        let! history =
            Service.timeline
                owner
                {
                    Target = target
                    Filter = Some TimelineFilter.Notes
                    AfterCursor = None
                    Limit = 20
                }
                token

        let history = read history
        Expect.equal history.Items.Length 5 "Notes filter includes four adds and removal only"

        Expect.isTrue
            (history.Items
             |> List.exists (fun entry ->
                 match entry.Changes with
                 | TimelineChanges.Disclosed value ->
                     value.Items
                     |> List.exists (function
                         | TimelineChange.Note(false, _, Some text) ->
                             NoteText.format text = "Note 111\nSecond line"
                         | _ -> false)
                 | _ -> false))
            "FULL history retains removed text"

        let! checkpoint = checkpoint database

        do!
            sql
                database
                "UPDATE claimcore.notes SET text='Changed projection' WHERE note_id=$1"
                [| box (id database.Fixture 110) |]

        do! finding VerificationFinding.CaseNotesProjection database
        do! enterMaintenance database

        let! repaired =
            ProjectionRebuild.rebuild
                database.OwnerSource
                database.MasterKey
                checkpoint
                (MaintenanceHandle.create
                    1L
                    (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
                 |> Option.get)
                token

        Expect.isOk repaired "Shared replay rebuilds current and removed notes"

        let! paused =
            Service.executeSubmission
                owner
                (note
                    (database.Fixture)
                    (122)
                    "note.add"
                    (None)
                    (Map.ofList
                        [
                            TaskInputId.Target InputTarget.NoteText,
                            ClaimCore.Application.Contracts.SubmissionValue.Text("Paused")
                        ]))
                token

        Expect.isTrue
            (match paused.Outcome with
             | ExecuteResult.Rejected Refusal.RegisterMaintenance -> true
             | _ -> false)
            "Maintenance pauses notes"

        let! mode =
            RegisterMaintenanceStore.change
                database.OwnerSource
                database.MasterKey
                (MaintenanceChange.Resume(
                    MaintenanceHandle.create
                        1L
                        (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
                    |> Option.get
                ))
                token

        Expect.isFalse mode.State.Active "Maintenance is off"

        let! closed =
            executeCase (database) (owner) (130) (1L) "case.close" (CaseTransitionInputs.closed)

        accepted closed |> ignore

        let! closedNote =
            Service.executeSubmission
                owner
                (note
                    (database.Fixture)
                    (131)
                    "note.add"
                    (None)
                    (Map.ofList
                        [
                            TaskInputId.Target InputTarget.NoteText,
                            ClaimCore.Application.Contracts.SubmissionValue.Text("After closing")
                        ]))
                token

        accepted closedNote |> ignore
        let! erased = executeCase (database) (owner) (132) (2L) "case.erase" (Map.empty)
        accepted erased |> ignore
        let! erasedNotes = notes reader None 20
        Expect.equal (read erasedNotes).Notes.Count 0L "Erasure clears all current text"

        let! removedAfterErasure =
            Service.operationGet
                owner
                {
                    OperationId = id database.Fixture 120
                }
                token

        Expect.isTrue
            (match read removedAfterErasure.Outcome with
             | OperationLookupResult.Accepted({ Subject = ReceiptSubject.Note value }, _) ->
                 value.Snapshot = NoteDisclosure.Erased
             | _ -> false)
            "The public removal remains accepted with erased disclosure"

        let! erasedHistory =
            Service.timeline
                owner
                {
                    Target = target
                    Filter = Some TimelineFilter.Notes
                    AfterCursor = None
                    Limit = 20
                }
                token

        Expect.isTrue
            ((read erasedHistory).Items
             |> List.forall (fun entry -> entry.Changes = TimelineChanges.Erased))
            "No historical note text survives erasure"

        let! _ = verified database
        return ()
    }

let private missingPayload database =
    task {
        let! owner = populated database

        let! added =
            Service.executeSubmission
                owner
                (note
                    (database.Fixture)
                    (150)
                    "note.add"
                    (None)
                    (Map.ofList
                        [
                            TaskInputId.Target InputTarget.NoteText,
                            ClaimCore.Application.Contracts.SubmissionValue.Text(
                                "Must remain until erasure"
                            )
                        ]))
                token

        accepted added |> ignore
        let! _ = verified database

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger_compartments SET payload=NULL,nonce=NULL,nonce_fingerprint=NULL WHERE operation_sequence=(SELECT sequence FROM claimcore.ledger WHERE operation_id=$1)"
                [| box (id database.Fixture 150) |]

        do! finding VerificationFinding.PayloadWithoutErasure database
    }

let private causalText database =
    task {
        let! owner = populated database

        let! added =
            Service.executeSubmission
                owner
                (note
                    (database.Fixture)
                    (160)
                    "note.add"
                    (None)
                    (Map.ofList
                        [
                            TaskInputId.Target InputTarget.NoteText,
                            ClaimCore.Application.Contracts.SubmissionValue.Text("Accepted text")
                        ]))
                token

        accepted added |> ignore
        let! _ = verified database
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! coordinates =
            DatabaseSession.rows
                connection
                "SELECT operation_sequence,ordinal,owner_kind,owner_id FROM claimcore.ledger_compartments WHERE operation_sequence=(SELECT sequence FROM claimcore.ledger WHERE operation_id=$1)"
                [| box (id database.Fixture 160) |]
                token

        let coordinates = List.exactlyOne coordinates
        let bytes = System.Text.Encoding.UTF8.GetBytes "\"Different valid text\""

        let ciphertext =
            database.Cipher.EncryptCompartment(
                DatabaseSession.value<int64> 0 coordinates,
                DatabaseSession.value<int> 1 coordinates,
                DatabaseSession.value<string> 2 coordinates,
                DatabaseSession.value<System.Guid> 3 coordinates,
                bytes
            )

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger_compartments SET payload=$2 WHERE operation_sequence=(SELECT sequence FROM claimcore.ledger WHERE operation_id=$1)"
                [| box (id database.Fixture 160); box ciphertext |]

        do! finding VerificationFinding.PayloadFormat database
    }

let private targetedLoading database =
    task {
        let! owner = populated database

        for number, role in [ 170, Role.Auditor; 171, Role.DataSteward ] do
            let! granted =
                grant
                    owner
                    (id database.Fixture number)
                    database.Fixture.Owner
                    {
                        Role = role
                        Scope = GrantTarget.Installation
                    }
                    true

            acceptedPeople granted

        for number in [ 180..182 ] do
            let! added =
                Service.executeSubmission
                    owner
                    (note
                        database.Fixture
                        number
                        "note.add"
                        None
                        (Map.ofList
                            [
                                TaskInputId.Target InputTarget.NoteText,
                                SubmissionValue.Text(sprintf "Private note %d" number)
                            ]))
                    token

            accepted added |> ignore

        for number in [ 183; 184 ] do
            let! planned =
                Service.executeSubmission
                    owner
                    (Submissions.createRaw
                        "reminder.create"
                        (id database.Fixture number)
                        (OperationBinding.Reminder(target, None))
                        (Submissions.reminderPlan (sprintf "Reminder %d" number) "2027-01-01"))
                    token

            match planned.Outcome with
            | ExecuteResult.Accepted _ -> ()
            | other -> failtestf "Reminder %d setup failed: %A" number other

        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT payload FROM claimcore.ledger_compartments WHERE operation_sequence=(SELECT sequence FROM claimcore.ledger WHERE operation_id=$1)"
                [| box (id database.Fixture 181) |]
                token

        let payload = DatabaseSession.value<byte array> 0 rows.Head

        let replace =
            "UPDATE claimcore.ledger_compartments SET payload=$2 WHERE operation_sequence=(SELECT sequence FROM claimcore.ledger WHERE operation_id=$1)"

        do!
            guardedSql
                database
                replace
                [| box (id database.Fixture 181); box (Array.zeroCreate<byte> payload.Length) |]

        let! reminderRows =
            DatabaseSession.rows
                connection
                "SELECT payload FROM claimcore.ledger_compartments WHERE operation_sequence=(SELECT sequence FROM claimcore.ledger WHERE operation_id=$1)"
                [| box (id database.Fixture 184) |]
                token

        let reminderPayload = DatabaseSession.value<byte array> 0 reminderRows.Head

        do!
            guardedSql
                database
                replace
                [|
                    box (id database.Fixture 184)
                    box (Array.zeroCreate<byte> reminderPayload.Length)
                |]

        let! closed =
            executeCase database owner 190 1L "case.close" CaseTransitionInputs.closed

        accepted closed |> ignore

        let removal =
            note database.Fixture 191 "note.remove" (Some(id database.Fixture 181)) Map.empty

        let! corrupt = Service.previewSubmission owner removal token

        Expect.equal
            corrupt.Outcome
            (Outcome.Failed CoreFault.StoreIntegrityError)
            "Selecting that same private root must still detect corruption"

        let reminderUpdate =
            Submissions.createRaw
                "reminder.update"
                (id database.Fixture 193)
                (OperationBinding.Reminder(target, Some(id database.Fixture 184, 1L)))
                (Submissions.reminderPlan "Changed action" "2027-01-02")

        let! corruptReminder = Service.previewSubmission owner reminderUpdate token

        Expect.equal
            corruptReminder.Outcome
            (Outcome.Failed CoreFault.StoreIntegrityError)
            "Selected reminder action roots remain hash checked"

        do! guardedSql database replace [| box (id database.Fixture 184); box reminderPayload |]
        do! guardedSql database replace [| box (id database.Fixture 181); box payload |]
        let! preview = Service.previewSubmission owner removal token

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed review) ->
            match review.Changes |> List.map _.Description with
            | [ ClaimCore.Domain.Changes.PartDescription.Note value ] ->
                Expect.equal
                    (value.Text |> Option.bind _.Before |> Option.map NoteText.format)
                    (Some "Private note 181")
                    "Only selected preceding text supplies the removal description"
            | other -> failtestf "Expected selected note description: %A" other
        | other -> failtestf "Expected note removal review: %A" other

        let! removed = Service.executeSubmission owner removal token
        accepted removed |> ignore

        let! history =
            Service.timeline
                owner
                {
                    Target = target
                    Filter = Some TimelineFilter.Notes
                    AfterCursor = None
                    Limit = 20
                }
                token

        Expect.equal
            (read history).Items.Length
            4
            "Three adds and the targeted removal remain readable"

        let! _ = verified database
        let! erased = executeCase database owner 192 2L "case.erase" Map.empty
        accepted erased |> ignore
        let! current = notes owner None 20

        Expect.equal
            (read current).Notes.Count
            0L
            "Bulk erasure clears unrelated note projections without acquiring bodies"

        let! _ = verified database
        return ()
    }

[<Tests>]
let tests =
    testList
        "PostgreSQL case notes"
        [
            testCaseAsync
                "unrelated decisions skip note bodies while selected review and erasure retain integrity"
                (run targetedLoading)
            testCaseAsync
                "current disclosure pagination removal maintenance erasure and rebuild"
                (run lifecycle)
            testCaseAsync "cleared NOTE payload requires case ERASE" (run missingPayload)
            testCaseAsync
                "NOTE content must match its independently retained commitment"
                (run causalText)
        ]
