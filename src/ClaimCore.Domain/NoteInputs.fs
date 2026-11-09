namespace ClaimCore.Domain

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module NoteInput =
    let private parseWith admit =
        function
        | AuthoredNoteAct.AddNote raw ->
            NoteText.parse raw
            |> Result.bind admit
            |> Result.map NoteInput.AddNote
            |> Result.mapError (fun violation ->
                Refusal.invalidInput (InputTarget.NoteText, violation))
        | AuthoredNoteAct.RemoveNote raw ->
            NoteId.create raw
            |> Option.map (NoteInput.RemoveNote >> Ok)
            |> Option.defaultValue (
                Error(Refusal.invalidInput (InputTarget.NoteId, Violation.NoteIdRequired))
            )

    let parse input = parseWith Ok input
    let admit input = parseWith NoteText.admit input

    let resolve parent =
        function
        | NoteInput.AddNote text -> NoteCommand.AddNote(parent, text)
        | NoteInput.RemoveNote id -> NoteCommand.RemoveNote(parent, id)

    let ofCommand =
        function
        | NoteCommand.AddNote(_, text) -> NoteInput.AddNote text
        | NoteCommand.RemoveNote(_, id) -> NoteInput.RemoveNote id

    let authored =
        function
        | NoteInput.AddNote text -> AuthoredNoteAct.AddNote(NoteText.format text)
        | NoteInput.RemoveNote id -> AuthoredNoteAct.RemoveNote(NoteId.value id)
