module ClaimCore.Tests.NoteDocumentTests

open System
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport

let private id =
    NoteId.create (Guid.Parse "00000000-0000-4000-8000-000000000001") |> Option.get

let private notes =
    {
        CaseNotes.Items =
            [
                {
                    NoteId = id
                    Text =
                        NoteText.parse "Core note narrative Ω"
                        |> Result.defaultWith (string >> failtest)
                    RecordedAt = Inputs.workBasis.ObservedAt
                    RecordedBy = ResponseExamples.caseRead.RecordedBy
                    CanRemove = true
                }
            ]
        Count = 1L
        CanAdd = true
    }

let private documents () =
    [
        HumanReadDocuments.notes
            {
                Basis = Inputs.workBasis
                CaseId = ResponseExamples.caseRead.Snapshot.CaseId
                Reference = ResponseExamples.caseRead.Snapshot.Reference
                Notes = notes
                NextCursor = Some "notes-next"
            }
        HumanCaseDocument.ofCore
            { ResponseExamples.caseRead with
                Notes = notes
            }
    ]

[<Tests>]
let tests =
    testList
        "note human controls"
        [
            testCase
                "case and paged note rows carry exact removal controls with their captions"
                (fun () ->
                    for document in documents () do
                        let rows =
                            document.Screen.Regions
                            |> List.collect (fun region ->
                                match region.Content with
                                | RegionContent.Table table when region.Role = RegionRole.Notes ->
                                    table.Rows
                                | _ -> [])

                        let row = List.exactlyOne rows
                        let action = List.exactlyOne row.Actions

                        Expect.equal
                            action.Task
                            (TaskId.Operation(ActKind.Note NoteActKind.RemoveNote))
                            "The removal task is supplied by Core"

                        Expect.equal
                            action.Binding
                            (OperationBinding.Note(
                                CaseSelector.Id ResponseExamples.caseRead.Snapshot.CaseId,
                                Some(NoteId.value id)
                            ))
                            "No row-key lookup binds the note"

                        Expect.isOk
                            (Codec.encode HumanDocumentCodecs.document document
                             |> Codec.decode 65536 HumanDocumentCodecs.document)
                            "Actual native boundary")
            testCase "read-only notes do not retain a removal control" (fun () ->
                let readOnly =
                    { notes with
                        Items =
                            notes.Items |> List.map (fun note -> { note with CanRemove = false })
                    }

                let document =
                    HumanReadDocuments.notes
                        {
                            Basis = Inputs.workBasis
                            CaseId = ResponseExamples.snapshot.CaseId
                            Reference = ResponseExamples.snapshot.Reference
                            Notes = readOnly
                            NextCursor = None
                        }

                let controls = Screen.affordances document.Screen
                Expect.isEmpty controls "The row's permission owns removal visibility")
        ]
