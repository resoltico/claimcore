namespace ClaimCore.Domain

open System

type NoteSubjectState =
    {
        NoteId: NoteId
        CaseId: CaseId
        Removed: bool
        Snapshot: Note option
        Created: SubjectStamp
        Erased: bool
    }

type NoteSubjectStructure =
    {
        Stamp: SubjectStamp
        CaseId: CaseId
        NoteId: NoteId
        Act: NoteActKind
    }

type NoteSubjectRows =
    {
        NoteId: NoteId
        CaseId: CaseId
        CreatedPosition: EffectPosition
        RecordedAt: DateTimeOffset
        RecordedBy: ActorId
        Text: NoteText option
    }

module NoteSubject =
    let private unavailable = Refusal.Access AccessRefusal.ResourceUnavailable

    let decide operation (parent: CaseSubjectState) (state: NoteSubjectState option) command =
        if NoteCommand.parent command <> parent.CaseId then
            Error unavailable
        elif parent.Lifecycle <> Lifecycle.Active then
            let headline =
                if parent.Lifecycle = Lifecycle.Erased then
                    CaseHeadline.Erased
                else
                    CaseHeadline.Voided

            Error(Refusal.Case(CaseRefusal.NotOffered headline))
        else
            match command, state with
            | NoteCommand.AddNote(_, _), Some _ -> Error Refusal.IdempotencyConflict
            | NoteCommand.AddNote(_, text), None ->
                NoteText.admit text
                |> Result.map (fun text ->
                    Note.ofAccepted (NoteId.ofOperationId operation) parent.CaseId (Some text))
                |> Result.mapError (fun violation ->
                    Refusal.invalidInput (InputTarget.NoteText, violation))
            | NoteCommand.RemoveNote(_, id), Some before when
                before.NoteId = id && before.CaseId = parent.CaseId && not before.Removed
                ->
                Ok(Note.ofAccepted id parent.CaseId None)
            | NoteCommand.RemoveNote _, _ -> Error unavailable

    let apply (stamp: SubjectStamp) (state: NoteSubjectState option) (effect: Note) =
        Some
            {
                NoteId = Note.id effect
                CaseId = Note.caseId effect
                Removed = Note.text effect |> Option.isNone
                Snapshot = Some effect
                Created = state |> Option.map _.Created |> Option.defaultValue stamp
                Erased = false
            }

    let applyErased
        (parent: CaseSubjectState)
        (state: NoteSubjectState option)
        (effect: NoteSubjectStructure)
        =
        if parent.Lifecycle <> Lifecycle.Active || parent.CaseId <> effect.CaseId then
            Error RecordViolation.InvalidSubjectState
        else
            match effect.Act, state with
            | NoteActKind.AddNote, None when
                effect.NoteId = NoteId.ofOperationId effect.Stamp.OperationId
                ->
                Ok(
                    Some
                        {
                            NoteId = effect.NoteId
                            CaseId = effect.CaseId
                            Removed = false
                            Snapshot = None
                            Created = effect.Stamp
                            Erased = false
                        }
                )
            | NoteActKind.RemoveNote, Some before when
                before.NoteId = effect.NoteId
                && before.CaseId = effect.CaseId
                && not before.Removed
                ->
                Ok(
                    Some
                        { before with
                            Removed = true
                            Snapshot = None
                        }
                )
            | _ -> Error RecordViolation.InvalidSubjectState

    let erase (state: NoteSubjectState) =
        { state with
            Snapshot = None
            Erased = true
        }

    let rows (state: NoteSubjectState) =
        if state.Snapshot.IsNone && not state.Removed && not state.Erased then
            None
        else
            Some
                {
                    NoteId = state.NoteId
                    CaseId = state.CaseId
                    CreatedPosition = state.Created.Position
                    RecordedAt = state.Created.RecordedAt
                    RecordedBy = state.Created.ActorId
                    Text = state.Snapshot |> Option.bind Note.text
                }
