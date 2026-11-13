namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module NotesScreen =
    let entries note =
        [
            value
                "noteText"
                "field.noteText.label"
                Prominence.Secondary
                (Note.text note |> optional (NoteText.format >> PresentedValue.Narrative))
        ]

    let table (notes: CaseNotes) =
        let definitions =
            [
                column
                    "noteText"
                    "field.noteText.label"
                    Prominence.Secondary
                    false
                    (fun (item: NoteReadModel) ->
                        PresentedValue.Narrative(NoteText.format item.Text))
                column "recordedBy" "label.recordedBy" Prominence.Primary false (fun item ->
                    PresentedValue.Actor item.RecordedBy)
                column "recordedAt" "label.recordedAt" Prominence.Primary false (fun item ->
                    PresentedValue.Instant item.RecordedAt)
            ]

        let table =
            ScreenEntries.table
                (fun (item: NoteReadModel) -> NoteId.format item.NoteId)
                (fun item ->
                    Some
                        {
                            Summary = None
                            Highlights = []
                            Sections =
                                [
                                    section
                                        "label.recordDetails"
                                        [
                                            value
                                                "noteId"
                                                "field.noteId.label"
                                                Prominence.Detail
                                                (PresentedValue.Identifier(
                                                    NoteId.format item.NoteId
                                                ))
                                        ]
                                ]
                        })
                definitions
                notes.Items

        { table with
            Sections =
                [
                    section
                        "label.notesCount"
                        [
                            value
                                "notesCount"
                                "label.notesCount"
                                Prominence.Secondary
                                (PresentedValue.Count notes.Count)
                        ]
                ]
        }

    let page (page: CaseNotesPage) =
        Screen.create
            [
                entryRegion
                    RegionRole.Identity
                    None
                    [
                        value
                            "caseReference"
                            "field.caseReference.label"
                            Prominence.Primary
                            (PresentedValue.Reference page.Reference)
                    ]
                region
                    RegionRole.Notes
                    (Some "section.notes.label")
                    (RegionContent.Table(table page.Notes))
            ]
