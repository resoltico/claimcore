namespace ClaimCore.Domain.Aggregates

open System
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs

type AcceptedPart<'value> =
    {
        Revision: Revision
        Stamp: SubjectStamp
        Value: 'value
    }

/// Notes contribute identity and removal state to decisions; their text stays in accepted values.
type NoteState =
    { Removed: bool; Created: SubjectStamp }

/// Decisions do not retain the accepted change's disposition reason.
type CasePartState =
    {
        Lifecycle: Lifecycle
        Status: CaseStatus
        Content: CaseContent option
    }

/// Due dates participate in the family index; an action is acquired only for a selected reminder.
type ReminderSchedule =
    | DueOnly of CalendarDate
    | AvailablePlan of ReminderPlan
    | Cleared

type ReminderFacts =
    {
        State: ReminderState
        Schedule: ReminderSchedule
    }

    member this.Plan =
        match this.Schedule with
        | AvailablePlan plan -> Some plan
        | _ -> None

    member this.Due =
        match this.Schedule with
        | AvailablePlan plan -> Some plan.Due
        | DueOnly due -> Some due
        | Cleared -> None

    member this.Value: ReminderValue = { State = this.State; Plan = this.Plan }

type CaseFamily =
    private
        {
            Identity: CaseId
            ReferenceValue: CaseReference
            CaseValue: AcceptedPart<CasePartState>
            PaymentsValue: Map<OperationId, AcceptedPart<PaymentValue>>
            WorkValue: AcceptedPart<CaseWork option> option
            RemindersValue: Map<ReminderId, AcceptedPart<ReminderFacts>>
            NotesValue: Map<NoteId, AcceptedPart<NoteState>>
            SequenceValue: Sequence
        }

    member this.Id = this.Identity
    member this.Reference = this.ReferenceValue
    member this.Case = this.CaseValue
    member this.Payments = this.PaymentsValue
    member this.Work = this.WorkValue
    member this.Reminders = this.RemindersValue
    member this.Notes = this.NotesValue
    member this.Sequence = this.SequenceValue
    member this.ChangedAt = this.CaseValue.Stamp.RecordedAt

module CaseFamily =
    let validateContent (content: CaseContent) =
        CaseAdmission.financial content.Registration content.Finance

    let validateSnapshot (snapshot: CaseSnapshot) =
        if snapshot.Revision < Revision.first then
            Error RecordViolation.RevisionOutOfRange
        else
            match CaseState.fields snapshot.State with
            | None -> Ok()
            | Some fields when fields.CaseReference <> snapshot.Reference ->
                Error RecordViolation.ReferenceMismatch
            | Some fields ->
                validateContent
                    {
                        Registration = fields.Registration
                        Finance = fields.Finance
                    }

    let private financial
        (payments: Map<OperationId, AcceptedPart<PaymentValue>>)
        (value: CaseValue)
        =
        match value.Content with
        | None -> Ok()
        | Some content ->
            validateContent content
            |> Result.bind (fun () ->
                let active =
                    payments
                    |> Map.values
                    |> Seq.filter (fun part -> not part.Value.Removed)
                    |> Seq.toList

                let entries = active |> List.choose _.Value.Entry
                let dates = entries |> List.map PaymentEntry.date |> List.sort

                let bounds =
                    match dates with
                    | [] -> None
                    | first :: _ -> PaymentDateBounds.create first (List.last dates)

                if
                    entries.Length <> active.Length
                    || int64 entries.Length <> content.Finance.PaymentCount
                    || (entries |> List.sumBy (PaymentEntry.amount >> Amount.value))
                       <> Amount.value content.Finance.PaidAmount
                    || bounds <> content.Finance.PaymentDates
                then
                    Error RecordViolation.InvalidPaymentState
                else
                    Ok())

    let private caseState (value: CaseValue) : CasePartState =
        {
            Lifecycle = value.Lifecycle
            Status = value.Status
            Content = value.Content
        }

    let private accepted (change: PartChange) value =
        {
            Revision = change.Revision
            Stamp = change.Stamp
            Value = value
        }

    let private version (previous: AcceptedPart<'value> option) (change: PartChange) =
        PartChange.successor
            (previous |> Option.map _.Revision |> Option.defaultValue Revision.zero)
            change.Revision

    let private clear state =
        { state with
            CaseValue =
                { state.Case with
                    Value = { state.Case.Value with Content = None }
                }
            PaymentsValue =
                state.Payments
                |> Map.map (fun _ part ->
                    { part with
                        Value = { part.Value with Entry = None }
                    })
            WorkValue = state.Work |> Option.map (fun part -> { part with Value = None })
            RemindersValue =
                state.Reminders
                |> Map.map (fun _ part ->
                    { part with
                        Value = { part.Value with Schedule = Cleared }
                    })
        }

    /// Structural application never invents missing private values or re-runs a task's business rules.
    let apply (state: CaseFamily option) (change: PartChange) =
        let invalid = Error RecordViolation.InvalidSubjectState

        match change.Aggregate with
        | AggregateReference.Person _ -> invalid
        | AggregateReference.Family id when
            change.PartId = Guid.Empty
            || not (PartChange.validErasure change)
            || (state |> Option.exists (fun state -> state.Id <> id))
            ->
            invalid
        | AggregateReference.Family id ->
            let changed =
                match state, change.Value with
                | None, PartValue.Case value when
                    change.PartId = CaseId.value id
                    && id = CaseId.ofOperationId change.Stamp.OperationId
                    && value.Lifecycle = Lifecycle.Active
                    && value.Status = CaseStatus.Opened
                    && change.Erases.IsNone
                    && value.Disposition.IsNone
                    && value.Reference.IsSome
                    && change.NaturalKey = (value.Reference |> Option.map CaseReference.format)
                    && change.Revision = Revision.first
                    ->
                    financial Map.empty value
                    |> Result.map (fun () ->
                        {
                            Identity = id
                            ReferenceValue = value.Reference.Value
                            CaseValue = accepted change (caseState value)
                            PaymentsValue = Map.empty
                            WorkValue = None
                            RemindersValue = Map.empty
                            NotesValue = Map.empty
                            SequenceValue = EffectPosition.sequence change.Stamp.Position
                        })
                | None, _ -> Error RecordViolation.InvalidHistoryStart
                | Some before, _ when
                    change.NaturalKey.IsSome
                    || before.Case.Value.Lifecycle = Lifecycle.Erased
                    || EffectPosition.sequence change.Stamp.Position < before.Sequence
                    ->
                    invalid
                | Some _, value when change.Erases.IsSome && PartValue.kind value <> PartKind.Case ->
                    invalid
                | Some before, PartValue.Case value when change.PartId = CaseId.value id ->
                    if not (version (Some before.Case) change) then
                        Error RecordViolation.InvalidRevisionSequence
                    elif value.Reference.IsSome then
                        invalid
                    elif
                        value.Lifecycle = Lifecycle.Erased
                        && (value.Content.IsSome || value.Disposition.IsSome)
                    then
                        invalid
                    elif
                        (value.Lifecycle = Lifecycle.Erased)
                        <> (change.Erases = Some(CompartmentOwner.Family id))
                    then
                        invalid
                    else
                        financial before.Payments value
                        |> Result.map (fun () ->
                            { before with
                                CaseValue = accepted change (caseState value)
                            })
                | Some before, PartValue.Payment value ->
                    match OperationId.create change.PartId with
                    | Some payment when version (Map.tryFind payment before.Payments) change ->
                        let previous = Map.tryFind payment before.Payments

                        let validTransition =
                            match previous with
                            | None -> not value.Removed && payment = change.Stamp.OperationId
                            | Some previous ->
                                not previous.Value.Removed
                                && value.Removed
                                && (match previous.Value.Entry, value.Entry with
                                    | Some before, Some after -> before = after
                                    | _ -> true)

                        if
                            not validTransition
                            || (value.Entry
                                |> Option.exists (fun entry -> PaymentEntry.id entry <> payment))
                        then
                            Error RecordViolation.InvalidPaymentState
                        else
                            Ok
                                { before with
                                    PaymentsValue =
                                        Map.add payment (accepted change value) before.Payments
                                }
                    | Some _ -> Error RecordViolation.InvalidRevisionSequence
                    | None -> Error RecordViolation.InvalidPaymentState
                | Some before, PartValue.Work value when change.PartId = CaseId.value id ->
                    if version before.Work change then
                        Ok
                            { before with
                                WorkValue = Some(accepted change value)
                            }
                    else
                        Error RecordViolation.InvalidRevisionSequence
                | Some before, PartValue.Reminder value ->
                    match ReminderId.create change.PartId with
                    | Some reminder when version (Map.tryFind reminder before.Reminders) change ->
                        let previous = Map.tryFind reminder before.Reminders

                        let validTransition =
                            match previous with
                            | None ->
                                value.State = ReminderState.Pending
                                && reminder = ReminderId.ofOperationId change.Stamp.OperationId
                            | Some previous ->
                                match previous.Value.State, value.State with
                                | ReminderState.Pending, ReminderState.Pending -> true
                                | ReminderState.Pending,
                                  (ReminderState.Completed | ReminderState.Cancelled) ->
                                    match previous.Value.Plan, value.Plan with
                                    | Some before, Some after -> before = after
                                    | _ -> true
                                | (ReminderState.Completed | ReminderState.Cancelled),
                                  ReminderState.Pending ->
                                    match previous.Value.Plan, value.Plan with
                                    | Some before, Some after -> before.Action = after.Action
                                    | _ -> true
                                | _ -> false

                        if not validTransition then
                            invalid
                        else
                            Ok
                                { before with
                                    RemindersValue =
                                        Map.add
                                            reminder
                                            (accepted
                                                change
                                                {
                                                    ReminderFacts.State = value.State
                                                    Schedule =
                                                        value.Plan
                                                        |> Option.map AvailablePlan
                                                        |> Option.defaultValue Cleared
                                                })
                                            before.Reminders
                                }
                    | Some _ -> Error RecordViolation.InvalidRevisionSequence
                    | None -> invalid
                | Some before, PartValue.Note value ->
                    match NoteId.create change.PartId with
                    | Some note when version (Map.tryFind note before.Notes) change ->
                        let previous = Map.tryFind note before.Notes

                        let validTransition =
                            match previous with
                            | None ->
                                not value.Removed
                                && note = NoteId.ofOperationId change.Stamp.OperationId
                            | Some previous -> not previous.Value.Removed && value.Removed

                        let created =
                            Map.tryFind note before.Notes
                            |> Option.map _.Value.Created
                            |> Option.defaultValue change.Stamp

                        if not validTransition || (value.Removed && value.Text.IsSome) then
                            invalid
                        else
                            Ok
                                { before with
                                    NotesValue =
                                        Map.add
                                            note
                                            (accepted
                                                change
                                                {
                                                    Removed = value.Removed
                                                    Created = created
                                                })
                                            before.Notes
                                }
                    | Some _ -> Error RecordViolation.InvalidRevisionSequence
                    | None -> invalid
                | _ -> invalid

            changed
            |> Result.map (fun state ->
                let state = if change.Erases.IsSome then clear state else state

                { state with
                    SequenceValue = EffectPosition.sequence change.Stamp.Position
                })

    let snapshot (state: CaseFamily) =
        let value = state.Case.Value

        let fields =
            value.Content
            |> Option.map (fun content ->
                {
                    CaseReference = state.Reference
                    Status = value.Status
                    Registration = content.Registration
                    Finance = content.Finance
                })

        match value.Lifecycle, fields with
        | Lifecycle.Erased, _ ->
            Some
                {
                    CaseId = state.Id
                    Reference = state.Reference
                    Revision = state.Case.Revision
                    State = CaseState.Erased
                }
        | lifecycle, Some fields ->
            Some
                {
                    CaseId = state.Id
                    Reference = state.Reference
                    Revision = state.Case.Revision
                    State = CaseState.ofLifecycle lifecycle fields
                }
        | _ -> None

    let index (state: CaseFamily) =
        snapshot state
        |> Option.map (fun snapshot ->
            let work =
                state.Work
                |> Option.bind (fun part ->
                    part.Value
                    |> Option.map (fun work ->
                        {
                            CaseId = state.Id
                            Revision = part.Revision
                            Work = work
                        }))

            let due =
                state.Reminders
                |> Map.values
                |> Seq.choose (fun part ->
                    if part.Value.State = ReminderState.Pending then
                        part.Value.Due
                    else
                        None)
                |> Seq.toList

            CaseIndex.create state.ChangedAt snapshot work due)

    /// Projection writers consume accepted values and aggregate-derived indexes in the same fold.
    let rows (state: CaseFamily) (changes: PartChange list) =
        if
            changes
            |> List.exists (fun change ->
                change.Aggregate <> AggregateReference.Family state.Id
                || change.Kind = PartKind.Person)
        then
            Error RecordViolation.InvalidSubjectState
        else
            match index state with
            | None -> Error RecordViolation.InvalidFinancialPosition
            | Some index ->
                let erased = state.Case.Value.Lifecycle = Lifecycle.Erased

                let missing =
                    (state.Work |> Option.exists (fun part -> part.Value.IsNone))
                    || (state.Payments |> Map.exists (fun _ part -> part.Value.Entry.IsNone))
                    || (state.Reminders |> Map.exists (fun _ part -> part.Value.Due.IsNone))
                    || (changes
                        |> List.exists (fun change ->
                            match change.Value with
                            | PartValue.Note value -> not value.Removed && value.Text.IsNone
                            | PartValue.Reminder value -> value.Plan.IsNone
                            | _ -> false))

                if missing && not erased then
                    Error RecordViolation.InvalidSubjectState
                else
                    Ok(changes, index)

    /// Restores exact accepted versions for live decisions; history succession is checked by apply.
    let private restoreParts
        (reminderDates: Map<ReminderId, CalendarDate>)
        includePrivate
        id
        reference
        sequence
        (parts: PartChange list)
        (origins: Map<PartKind * Guid, SubjectStamp>)
        =
        let invalid = Error RecordViolation.InvalidSubjectState

        let cases =
            parts
            |> List.choose (fun change ->
                match change.Value with
                | PartValue.Case value -> Some(change, value)
                | _ -> None)

        let validIdentity (change: PartChange) =
            change.Aggregate = AggregateReference.Family id
            && change.PartId <> Guid.Empty
            && change.Revision >= Revision.first
            && EffectPosition.sequence change.Stamp.Position <= sequence
            && PartChange.validErasure change
            && (match Map.tryFind (change.Kind, change.PartId) origins with
                | Some origin ->
                    EffectPosition.sequence origin.Position
                    <= EffectPosition.sequence change.Stamp.Position
                    && (match change.Kind with
                        | PartKind.Case ->
                            change.PartId = CaseId.value id
                            && id = CaseId.ofOperationId origin.OperationId
                        | PartKind.Work -> change.PartId = CaseId.value id
                        | PartKind.Payment
                        | PartKind.Reminder
                        | PartKind.Note -> change.PartId = OperationId.value origin.OperationId
                        | PartKind.Person -> false)
                | None -> false)

        if
            parts |> List.exists (validIdentity >> not)
            || (parts
                |> List.map (fun part -> part.Kind, part.PartId)
                |> List.distinct
                |> List.length)
               <> parts.Length
        then
            invalid
        else
            match cases with
            | [ case, value ] when value.Reference |> Option.forall ((=) reference) ->
                let payments =
                    parts
                    |> List.choose (fun change ->
                        match change.Value, OperationId.create change.PartId with
                        | PartValue.Payment value, Some payment ->
                            Some(payment, accepted change value)
                        | _ -> None)
                    |> Map.ofList

                let work =
                    parts
                    |> List.tryPick (fun change ->
                        match change.Value with
                        | PartValue.Work value -> Some(accepted change value)
                        | _ -> None)

                let reminders =
                    parts
                    |> List.choose (fun change ->
                        match change.Value, ReminderId.create change.PartId with
                        | PartValue.Reminder value, Some reminder ->
                            Some(
                                reminder,
                                accepted
                                    change
                                    {
                                        ReminderFacts.State = value.State
                                        Schedule =
                                            match
                                                value.Plan, Map.tryFind reminder reminderDates
                                            with
                                            | Some plan, Some due when plan.Due <> due -> Cleared
                                            | Some plan, _ -> AvailablePlan plan
                                            | None, Some due -> DueOnly due
                                            | None, None -> Cleared
                                    }
                            )
                        | _ -> None)
                    |> Map.ofList

                let notes =
                    parts
                    |> List.choose (fun change ->
                        match change.Value, NoteId.create change.PartId with
                        | PartValue.Note value, Some note ->
                            Some(
                                note,
                                accepted
                                    change
                                    {
                                        Removed = value.Removed
                                        Created = Map.find (change.Kind, change.PartId) origins
                                    }
                            )
                        | _ -> None)
                    |> Map.ofList

                let state =
                    {
                        Identity = id
                        ReferenceValue = reference
                        CaseValue = accepted case (caseState value)
                        PaymentsValue = payments
                        WorkValue = work
                        RemindersValue = reminders
                        NotesValue = notes
                        SequenceValue = sequence
                    }

                financial payments value
                |> Result.bind (fun () ->
                    let acquired =
                        parts |> List.filter (fun part -> includePrivate part.Kind part.PartId)

                    rows state acquired |> Result.map (fun _ -> state))
            | _ -> invalid

    /// Full reconstruction requires all still-present private values.
    let restore id reference sequence parts origins =
        restoreParts Map.empty (fun _ _ -> true) id reference sequence parts origins

    /// Live acquisition carries note identity/removal and reminder dates without unrelated text.
    let restoreForDecision dates includePrivate id reference sequence parts origins =
        restoreParts dates includePrivate id reference sequence parts origins
