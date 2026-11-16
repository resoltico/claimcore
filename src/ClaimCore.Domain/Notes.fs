namespace ClaimCore.Domain

open System

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type NoteId = private NoteId of Guid

module NoteId =
    let create value =
        if value = Guid.Empty then None else Some(NoteId value)

    let ofOperationId operation = NoteId(OperationId.value operation)
    let value (NoteId value) = value
    let format id = (value id).ToString("D")

/// Removal retains identity and clears the current text; accepted history remains separate.
type Note =
    private
        {
            Id: NoteId
            CaseId: CaseId
            Text: NoteText option
        }

module Note =
    let id (note: Note) = note.Id
    let caseId (note: Note) = note.CaseId
    let text (note: Note) = note.Text

    let ofAccepted id parent text =
        {
            Id = id
            CaseId = parent
            Text = text
        }
