module ClaimCore.Tests.NotesTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Tests.Fixtures

let private operation =
    OperationId.create (Guid.Parse "20000000-0000-7000-8000-000000000071")
    |> Option.get

let private other =
    OperationId.create (Guid.Parse "20000000-0000-7000-8000-000000000072")
    |> Option.get

let private text value = NoteText.parse value |> accepted
let private parent () = opened () |> Case.snapshot

let private add parent value =
    Note.decide operation parent None (NoteCommand.AddNote(parent.CaseId, text value))

[<Tests>]
let tests =
    testList
        "case notes"
        [
            testCase
                "text admits 2000 Unicode scalars including LF and supplementary characters"
                (fun () ->
                    let value = String.replicate 1998 "😀" + "\nA"

                    Expect.equal
                        (NoteText.parse value
                         |> Result.bind NoteText.admit
                         |> Result.map NoteText.format)
                        (Ok value)
                        "Scalars, not UTF-16 code units"

                    Expect.isError
                        (NoteText.parse (String.replicate 2001 "A") |> Result.bind NoteText.admit)
                        "Scalar ceiling"

                    Expect.isError
                        (NoteText.parse (String.replicate 2049 "😀"))
                        "UTF-8 byte ceiling"

                    Expect.isError (NoteText.parse "") "At least one scalar"

                    Expect.isError
                        (NoteText.parse "line\rbreak" |> Result.bind NoteText.admit)
                        "Only LF is permitted"

                    Expect.equal
                        (NoteText.parse " indented"
                         |> Result.bind NoteText.admit
                         |> Result.map NoteText.format)
                        (Ok " indented")
                        "Narrative indentation is preserved")
            testCase
                "add derives identity from accepting operation and leaves parent revision unchanged"
                (fun () ->
                    let parent = parent ()
                    let note = add parent "Keep this note" |> accepted

                    Expect.equal
                        (Note.id note)
                        (NoteId.ofOperationId operation)
                        "Stable accepted identity"

                    Expect.equal (Note.caseId note) parent.CaseId "Parent scoped"

                    Expect.equal
                        (Note.text note |> Option.map NoteText.format)
                        (Some "Keep this note")
                        "Exact text"

                    Expect.equal parent.Revision Revision.first "No case revision is consumed"

                    Expect.equal
                        (Note.decide
                            other
                            parent
                            (Some note)
                            (NoteCommand.AddNote(parent.CaseId, text "Again")))
                        (Error NoteRefusal.IdentityReserved)
                        "No overwrite")
            testCase
                "closed active case permits notes but voided and erased parents refuse"
                (fun () ->
                    let record =
                        opened () |> apply (AuthoredCaseAct.Bare BareAct.Close) |> accepted

                    let closed = Case.snapshot record
                    Expect.isOk (add closed "After closing") "Notes do not reopen the case"

                    let voided =
                        { closed with
                            State = CaseState.Voided(viewFields closed)
                        }

                    Expect.equal (add voided "No") (Error NoteRefusal.ParentNotActive) "Voided"

                    Expect.equal
                        (add { closed with State = CaseState.Erased } "No")
                        (Error NoteRefusal.ParentNotActive)
                        "Erased")
            testCase
                "remove clears current text exactly once and rejects mismatched identity or parent"
                (fun () ->
                    let parent = parent ()
                    let note = add parent "Historical text" |> accepted
                    let command = NoteCommand.RemoveNote(parent.CaseId, Note.id note)
                    let removed = Note.decide other parent (Some note) command |> accepted
                    Expect.isNone (Note.text removed) "Current text is gone"

                    Expect.equal
                        (Note.decide other parent (Some removed) command)
                        (Error NoteRefusal.NotOffered)
                        "Removed is not offered"

                    Expect.equal
                        (Note.decide other parent None command)
                        (Error NoteRefusal.NotOffered)
                        "Missing is not offered"

                    Expect.equal
                        (Note.decide
                            other
                            parent
                            (Some note)
                            (NoteCommand.RemoveNote(parent.CaseId, NoteId.ofOperationId other)))
                        (Error NoteRefusal.NotOffered)
                        "Wrong note"

                    let otherParent =
                        CaseId.create (Guid.Parse "10000000-0000-4000-8000-000000000002")
                        |> Option.get

                    Expect.equal
                        (Note.decide
                            other
                            parent
                            (Some note)
                            (NoteCommand.RemoveNote(otherParent, Note.id note)))
                        (Error NoteRefusal.ParentMismatch)
                        "Wrong parent")
            testCase
                "canonical notes retain authored input and accepted removal without a revision"
                (fun () ->
                    let parent = parent ()
                    let note = add parent "First\nSecond" |> accepted

                    let request =
                        {
                            NoteRequestRecord.OperationId = operation
                            Command = NoteCommand.AddNote(parent.CaseId, text "First\nSecond")
                        }

                    let bytes = NoteRecord.encodeRequest request

                    let literal =
                        """{"act":{"kind":"ADD_NOTE","text":"First\nSecond"},"caseId":"20000000-0000-7000-8000-000000000001","operationId":"20000000-0000-7000-8000-000000000071"}"""

                    Expect.equal
                        bytes
                        (System.Text.Encoding.UTF8.GetBytes literal)
                        "Independent canonical request bytes"

                    Expect.equal (NoteRecord.decodeRequest bytes) (Ok request) "Canonical request"

                    Expect.isFalse
                        (System.Text.Encoding.UTF8
                            .GetString(bytes)
                            .Contains("revision", StringComparison.OrdinalIgnoreCase))
                        "No revision contract"

                    Expect.equal
                        (NoteRecord.decodeSnapshot (NoteRecord.encodeSnapshot note))
                        (Ok note)
                        "Accepted text"

                    let removed =
                        Note.decide
                            other
                            parent
                            (Some note)
                            (NoteCommand.RemoveNote(parent.CaseId, Note.id note))
                        |> accepted

                    Expect.equal
                        (NoteRecord.decodeSnapshot (NoteRecord.encodeSnapshot removed))
                        (Ok removed)
                        "Removed state")
        ]
