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

[<RequireQualifiedAccess>]
type NoteActKind =
    | AddNote
    | RemoveNote

module NoteActKind =
    let vocabulary =
        Vocabulary.ofUnion<NoteActKind> "noteAct" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type AuthoredNoteAct =
    | AddNote of text: string
    | RemoveNote of noteId: Guid

module AuthoredNoteAct =
    let kind =
        function
        | AuthoredNoteAct.AddNote _ -> NoteActKind.AddNote
        | AuthoredNoteAct.RemoveNote _ -> NoteActKind.RemoveNote

[<RequireQualifiedAccess>]
type NoteInput =
    | AddNote of NoteText
    | RemoveNote of NoteId

[<RequireQualifiedAccess>]
type NoteCommand =
    | AddNote of parent: CaseId * NoteText
    | RemoveNote of parent: CaseId * NoteId

module NoteCommand =
    let parent =
        function
        | NoteCommand.AddNote(parent, _)
        | NoteCommand.RemoveNote(parent, _) -> parent

    let kind =
        function
        | NoteCommand.AddNote _ -> NoteActKind.AddNote
        | NoteCommand.RemoveNote _ -> NoteActKind.RemoveNote

    let target operation =
        function
        | NoteCommand.AddNote _ -> NoteId.ofOperationId operation
        | NoteCommand.RemoveNote(_, id) -> id

/// Removal retains identity and clears the current text; accepted history remains separate.
type Note =
    private
        {
            Id: NoteId
            CaseId: CaseId
            Text: NoteText option
        }

[<RequireQualifiedAccess>]
type NoteRefusal =
    | ParentMismatch
    | ParentNotActive
    | NotOffered
    | IdentityReserved
    | InvalidText of Violation

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

    let decide operation (parent: CaseSnapshot) before command =
        if NoteCommand.parent command <> parent.CaseId then
            Error NoteRefusal.ParentMismatch
        else
            match parent.State with
            | CaseState.Erased
            | CaseState.Voided _ -> Error NoteRefusal.ParentNotActive
            | CaseState.Active _ ->
                match command, before with
                | NoteCommand.AddNote(_, _), Some _ -> Error NoteRefusal.IdentityReserved
                | NoteCommand.AddNote(_, text), None ->
                    NoteText.admit text
                    |> Result.map (fun text ->
                        ofAccepted (NoteId.ofOperationId operation) parent.CaseId (Some text))
                    |> Result.mapError NoteRefusal.InvalidText
                | NoteCommand.RemoveNote(_, id), Some note when
                    note.Id = id && note.CaseId = parent.CaseId && note.Text.IsSome
                    ->
                    Ok { note with Text = None }
                | NoteCommand.RemoveNote _, _ -> Error NoteRefusal.NotOffered
