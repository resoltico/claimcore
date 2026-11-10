module ClaimCore.IntegrationTests.NotesPostgresTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private target = CaseSelector.Reference "synthetic-a"

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected read: %A" other

let private note fixture number act =
    OperationInput.Note
        {
            OperationId = id fixture number
            Target = target
            Act = act
        }

let private notes service cursor limit =
    Service.notes
        service
        {
            Target = target
            AfterCursor = cursor
            Limit = limit
        }
        token

let private executeCase (database: PostgresFixture.Database) service number revision act =
    Service.execute
        service
        (Inputs.caseOperation (request database.Fixture number "synthetic-a" revision act))
        token

let private lifecycle database =
    task {
        let! owner = populated database

        let readerPrincipal =
            PrincipalKey.human "https://issuer.example/" "note-reader"
            |> Result.defaultWith (string >> invalidOp)

        let! registered =
            Service.execute
                owner
                (OperationInput.Authority(
                    Inputs.authorityRequest
                        (id database.Fixture 100)
                        readerPrincipal
                        (AuthorityAct.Register Inputs.actorProfile)
                ))
                token

        acceptedAuthority registered

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

        acceptedAuthority granted
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

        acceptedAuthority steward

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

        acceptedAuthority auditor

        for number in [ 110..113 ] do
            let! acceptedNote =
                Service.execute
                    owner
                    (note
                        database.Fixture
                        number
                        (AuthoredNoteAct.AddNote(sprintf "Note %d\nSecond line" number)))
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
            Service.execute
                owner
                (note database.Fixture 120 (AuthoredNoteAct.RemoveNote(id database.Fixture 111)))
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
            Service.execute
                owner
                (note database.Fixture 121 (AuthoredNoteAct.RemoveNote(id database.Fixture 111)))
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
                         | TimelineChange.Note(NoteActKind.AddNote, _, Some text) ->
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
        let! repaired = ProjectionRebuild.rebuild database.OwnerSource checkpoint token
        Expect.isOk repaired "Shared replay rebuilds current and removed notes"

        let! paused =
            Service.execute
                owner
                (note database.Fixture 122 (AuthoredNoteAct.AddNote "Paused"))
                token

        Expect.isTrue
            (match paused.Outcome with
             | ExecuteResult.Rejected Refusal.RegisterMaintenance -> true
             | _ -> false)
            "Maintenance pauses notes"

        let! mode =
            RegisterMaintenanceStore.change database.OwnerSource MaintenanceChange.Off token

        Expect.isFalse mode.State.Active "Maintenance is off"
        let! closed = executeCase database owner 130 1L (AuthoredCaseAct.Bare BareAct.Close)
        accepted closed |> ignore

        let! closedNote =
            Service.execute
                owner
                (note database.Fixture 131 (AuthoredNoteAct.AddNote "After closing"))
                token

        accepted closedNote |> ignore
        let! erased = executeCase database owner 132 2L (AuthoredCaseAct.Bare BareAct.Erase)
        accepted erased |> ignore
        let! erasedNotes = notes reader None 20
        Expect.equal (read erasedNotes).Notes.Count 0L "Erasure clears all current text"

        let! removedAfterErasure =
            Service.operationGet owner { OperationId = id database.Fixture 120 } token

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
            Service.execute
                owner
                (note database.Fixture 150 (AuthoredNoteAct.AddNote "Must remain until erasure"))
                token

        accepted added |> ignore
        let! _ = verified database

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger_compartments SET payload=NULL,nonce=NULL WHERE operation_sequence=(SELECT sequence FROM claimcore.ledger WHERE operation_id=$1)"
                [| box (id database.Fixture 150) |]

        do! finding VerificationFinding.PayloadWithoutErasure database
    }

let private causalText database =
    task {
        let! owner = populated database

        let! added =
            Service.execute
                owner
                (note database.Fixture 160 (AuthoredNoteAct.AddNote "Accepted text"))
                token

        accepted added |> ignore
        let! _ = verified database

        do!
            guardedSql database
                "UPDATE claimcore.ledger_compartments SET payload=convert_to($2,'UTF8') WHERE operation_sequence=(SELECT sequence FROM claimcore.ledger WHERE operation_id=$1)"
                [| box (id database.Fixture 160); box "\"Different valid text\"" |]

        do! finding VerificationFinding.PayloadFormat database
    }

[<Tests>]
let tests =
    testList
        "PostgreSQL case notes"
        [
            testCaseAsync
                "current disclosure pagination removal maintenance erasure and rebuild"
                (run lifecycle)
            testCaseAsync "cleared NOTE payload requires case ERASE" (run missingPayload)
            testCaseAsync "NOTE content must match its independently retained commitment" (run causalText)
        ]
