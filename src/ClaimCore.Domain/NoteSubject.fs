namespace ClaimCore.Domain

open System

type NoteSubjectState =
    {
        NoteId: NoteId
        CaseId: CaseId
        Removed: bool option
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

    let private transition operation before command =
        match command, before with
        | NoteCommand.AddNote(parent, text), None ->
            Ok(Note.ofAccepted (NoteId.ofOperationId operation) parent (Some text))
        | NoteCommand.AddNote _, Some _ -> Error Refusal.IdempotencyConflict
        | NoteCommand.RemoveNote(parent, id), Some(beforeId, beforeParent, false) when
            beforeId = id && beforeParent = parent
            ->
            Ok(Note.ofAccepted id parent None)
        | _ -> Error unavailable

    let private applySnapshot before after =
        let next = Note.id after, Note.caseId after, Note.text after |> Option.isNone

        match before, next with
        | None, (_, _, false) -> Ok after
        | Some(id, parent, false), (nextId, nextParent, true) when
            id = nextId && parent = nextParent
            ->
            Ok after
        | _ -> Error RecordViolation.InvalidSubjectState

    let applyRecorded operation before command after =
        let prior =
            before
            |> Option.map (fun note ->
                Note.id note, Note.caseId note, Note.text note |> Option.isNone)

        transition operation prior command
        |> Result.mapError (fun _ -> RecordViolation.InvalidSubjectState)
        |> Result.bind (fun expected ->
            if expected = after then
                applySnapshot prior after
            else
                Error RecordViolation.InvalidSubjectState)

    let decide
        context
        operation
        (parent: CaseSubjectState)
        (state: NoteSubjectState option)
        command
        =
        DecisionContext.admit
            context
            operation
            (DecisionContext.workAccess context parent.CaseId)
            (fun () -> Ok())
        |> Result.bind (fun () ->
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
                let admission =
                    match command, context.Lifetime with
                    | NoteCommand.AddNote(_, text), lifetime when
                        lifetime <> DecisionLifetime.Recorded
                        ->
                        NoteText.admit text
                        |> Result.map ignore
                        |> Result.mapError (fun violation ->
                            Refusal.invalidInput (InputTarget.NoteText, violation))
                    | _ -> Ok()

                admission
                |> Result.bind (fun () ->
                    transition
                        operation
                        (state
                         |> Option.map (fun before ->
                             before.NoteId,
                             before.CaseId,
                             before.Removed |> Option.defaultValue true))
                        command)

        )

    let apply (stamp: SubjectStamp) (state: NoteSubjectState option) (effect: Note) =
        applySnapshot
            (state
             |> Option.map (fun before ->
                 before.NoteId, before.CaseId, before.Removed |> Option.defaultValue true))
            effect
        |> Result.defaultWith (SubjectStateInvalid >> raise)
        |> ignore

        Some
            {
                NoteId = Note.id effect
                CaseId = Note.caseId effect
                Removed = Some(Note.text effect |> Option.isNone)
                Snapshot = Some effect
                Created = state |> Option.map _.Created |> Option.defaultValue stamp
                Erased = false
            }

    let applyErased
        context
        (parent: CaseSubjectState)
        (state: NoteSubjectState option)
        (effect: NoteSubjectStructure)
        =
        DecisionContext.admit
            context
            effect.Stamp.OperationId
            (DecisionContext.workAccess context parent.CaseId)
            (fun () -> Ok())
        |> Result.mapError (fun _ -> RecordViolation.InvalidSubjectState)
        |> Result.bind (fun () ->
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
                                Removed = Some false
                                Snapshot = None
                                Created = effect.Stamp
                                Erased = false
                            }
                    )
                | NoteActKind.RemoveNote, Some before when
                    before.NoteId = effect.NoteId
                    && before.CaseId = effect.CaseId
                    && before.Removed = Some false
                    ->
                    Ok(
                        Some
                            { before with
                                Removed = Some true
                                Snapshot = None
                            }
                    )
                | _ -> Error RecordViolation.InvalidSubjectState)

    let erase (state: NoteSubjectState) =
        { state with
            Snapshot = None
            Erased = true
        }

    let rows (state: NoteSubjectState) =
        if state.Snapshot.IsNone && state.Removed <> Some true && not state.Erased then
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

    let restore caseId lifecycle (row: NoteSubjectRows) =
        match OperationId.create (NoteId.value row.NoteId) with
        | Some operation when
            row.CaseId = caseId && (lifecycle <> Lifecycle.Erased || row.Text.IsNone)
            ->
            Ok
                {
                    NoteId = row.NoteId
                    CaseId = caseId
                    Removed =
                        if lifecycle = Lifecycle.Erased then
                            None
                        else
                            Some row.Text.IsNone
                    Snapshot =
                        if lifecycle = Lifecycle.Erased then
                            None
                        else
                            Some(Note.ofAccepted row.NoteId caseId row.Text)
                    Created =
                        {
                            Position = row.CreatedPosition
                            OperationId = operation
                            ActorId = row.RecordedBy
                            RecordedAt = row.RecordedAt
                        }
                    Erased = lifecycle = Lifecycle.Erased
                }
        | _ -> Error RecordViolation.InvalidSubjectState
