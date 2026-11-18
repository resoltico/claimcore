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
        Handling: Handling
        Content: CaseContent option
    }

    member this.Status = Handling.status this.Handling

type internal FamilyParts =
    {
        Identity: CaseId
        ReferenceValue: CaseReference
        CaseValue: AcceptedPart<CasePartState>
        PaymentsValue: Map<OperationId, AcceptedPart<PaymentValue>>
        WorkValue: AcceptedPart<CaseWork option> option
        RemindersValue: Map<ReminderId, AcceptedPart<ReminderValue>>
        NotesValue: Map<NoteId, AcceptedPart<NoteState>>
        PartiesValue: Map<PartyId, AcceptedPart<PartyValue>>
        PartyOrigins: Map<PartyId, EffectPosition>
        SequenceValue: Sequence
    }

    member this.Id = this.Identity
    member this.Reference = this.ReferenceValue
    member this.Case = this.CaseValue
    member this.Payments = this.PaymentsValue
    member this.Work = this.WorkValue
    member this.Reminders = this.RemindersValue
    member this.Notes = this.NotesValue
    member this.Parties = this.PartiesValue
    member this.Sequence = this.SequenceValue
    member this.ChangedAt = this.CaseValue.Stamp.RecordedAt

/// Complete reconstruction for replay, verification and rebuild. Live acquisition cannot construct this type.
type CaseFamily =
    private
    | CaseFamily of FamilyParts

    member internal this.Value = let (CaseFamily value) = this in value
    member this.Id = this.Value.Id
    member this.Reference = this.Value.Reference
    member this.Case = this.Value.Case
    member this.Payments = this.Value.Payments
    member this.Work = this.Value.Work
    member this.Reminders = this.Value.Reminders
    member this.Notes = this.Value.Notes
    member this.Parties = this.Value.Parties
    member this.Sequence = this.Value.Sequence
    member this.ChangedAt = this.Value.ChangedAt

module internal FamilyParts =
    let validateSnapshot (snapshot: CaseSnapshot) =
        if snapshot.Revision < Revision.first then
            Error RecordViolation.RevisionOutOfRange
        else
            match CaseState.fields snapshot.State with
            | None -> Ok()
            | Some fields when fields.CaseReference <> snapshot.Reference ->
                Error RecordViolation.ReferenceMismatch
            | Some _ -> Ok()

    let private financial
        (payments: Map<OperationId, AcceptedPart<PaymentValue>>)
        (value: CaseValue)
        =
        match value.Content with
        | None -> Ok()
        | Some content ->
            let active =
                payments
                |> Map.values
                |> Seq.filter (fun part -> not part.Value.Removed)
                |> Seq.toList

            let entries = active |> List.choose _.Value.Entry

            match FinancialPosition.rebuild entries with
            | Ok finance when
                entries.Length = active.Length
                && finance.PaymentCount = content.Finance.PaymentCount
                && finance.CurrencyTotals = content.Finance.CurrencyTotals
                ->
                Ok()
            | _ -> Error RecordViolation.InvalidPaymentState

    let private caseState (value: CaseValue) : CasePartState =
        {
            Lifecycle = value.Lifecycle
            Handling = value.Handling
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

    let private partyId (caseStamp: SubjectStamp) (origin: SubjectStamp) =
        if origin.OperationId = caseStamp.OperationId then
            let ordinal = EffectPosition.index origin.Position - 1

            if ordinal >= 0 && ordinal < PartyContent.MaximumCurrent then
                Some(PartyId.derive origin.OperationId ordinal)
            else
                None
        else
            Some(PartyId.ofOperationId origin.OperationId)

    let currentParties (state: FamilyParts) =
        state.Parties
        |> Map.toSeq
        |> Seq.filter (fun (_, part) -> not part.Value.Removed)
        |> Seq.sortBy (fun (id, _) -> Map.find id state.PartyOrigins)
        |> Seq.choose (fun (_, part) -> part.Value.Content)
        |> Seq.toList

    let private clear state =
        { state with
            CaseValue =
                { state.Case with
                    Value =
                        { state.Case.Value with
                            Content = None
                            Handling = Handling.clearNote state.Case.Value.Handling
                        }
                }
            PaymentsValue =
                state.Payments
                |> Map.map (fun _ part ->
                    { part with
                        Value = { part.Value with Entry = None }
                    })
            PartiesValue =
                state.Parties
                |> Map.map (fun _ part ->
                    { part with
                        Value = { part.Value with Content = None }
                    })
            WorkValue = state.Work |> Option.map (fun part -> { part with Value = None })
            RemindersValue =
                state.Reminders
                |> Map.map (fun _ part ->
                    { part with
                        Value = { part.Value with Plan = None }
                    })
        }

    /// Structural application never invents missing private values or re-runs a task's business rules.
    let applyWith financialCheck (state: FamilyParts option) (change: PartChange) =
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
                    financialCheck Map.empty value
                    |> Result.map (fun () ->
                        {
                            Identity = id
                            ReferenceValue = value.Reference.Value
                            CaseValue = accepted change (caseState value)
                            PaymentsValue = Map.empty
                            WorkValue = None
                            RemindersValue = Map.empty
                            NotesValue = Map.empty
                            PartiesValue = Map.empty
                            PartyOrigins = Map.empty
                            SequenceValue = EffectPosition.sequence change.Stamp.Position
                        })
                | None, _ -> Error RecordViolation.InvalidHistoryStart
                | Some before, _ when
                    (change.NaturalKey.IsSome && change.Kind <> PartKind.Case)
                    || before.Case.Value.Lifecycle = Lifecycle.Erased
                    || EffectPosition.sequence change.Stamp.Position < before.Sequence
                    ->
                    invalid
                | Some _, value when change.Erases.IsSome && PartValue.kind value <> PartKind.Case ->
                    invalid
                | Some before, PartValue.Case value when change.PartId = CaseId.value id ->
                    if not (version (Some before.Case) change) then
                        Error RecordViolation.InvalidRevisionSequence
                    elif
                        change.NaturalKey
                        |> Option.exists (fun key ->
                            value.Reference |> Option.map CaseReference.format <> Some key)
                    then
                        invalid
                    elif
                        value.Reference.IsSome
                        && (value.Reference = Some before.Reference
                            || value.Lifecycle <> before.Case.Value.Lifecycle
                            || value.Status <> before.Case.Value.Status
                            || value.Content <> before.Case.Value.Content
                            || value.Disposition.IsSome)
                    then
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
                        financialCheck before.Payments value
                        |> Result.map (fun () ->
                            { before with
                                CaseValue = accepted change (caseState value)
                                ReferenceValue = defaultArg value.Reference before.Reference
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
                                        Map.add reminder (accepted change value) before.Reminders
                                }
                    | Some _ -> Error RecordViolation.InvalidRevisionSequence
                    | None -> invalid
                | Some before, PartValue.Party value ->
                    match PartyId.create change.PartId with
                    | Some party when version (Map.tryFind party before.Parties) change ->
                        let valid =
                            match Map.tryFind party before.Parties with
                            | None ->
                                not value.Removed
                                && partyId before.Case.Stamp change.Stamp = Some party
                            | Some previous ->
                                not previous.Value.Removed
                                && (not value.Removed || previous.Value.Content = value.Content)

                        if valid then
                            let origins =
                                if Map.containsKey party before.PartyOrigins then
                                    before.PartyOrigins
                                else
                                    Map.add party change.Stamp.Position before.PartyOrigins

                            Ok
                                { before with
                                    PartiesValue =
                                        Map.add party (accepted change value) before.Parties
                                    PartyOrigins = origins
                                }
                        else
                            invalid
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

    let snapshot (state: FamilyParts) =
        let value = state.Case.Value

        let fields =
            value.Content
            |> Option.map (fun content ->
                {
                    CaseReference = state.Reference
                    Handling = value.Handling
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

    let index (state: FamilyParts) =
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
                        (part.Value.Plan |> Option.map _.Due)
                    else
                        None)
                |> Seq.toList

            CaseIndex.create state.ChangedAt snapshot work due (currentParties state))

    /// Projection writers consume accepted values and aggregate-derived indexes in the same fold.
    let rows (state: FamilyParts) (changes: PartChange list) =
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
                    || (state.Reminders
                        |> Map.exists (fun _ part -> (part.Value.Plan |> Option.map _.Due).IsNone))
                    || (changes
                        |> List.exists (fun change ->
                            match change.Value with
                            | PartValue.Note value -> not value.Removed && value.Text.IsNone
                            | PartValue.Party value -> value.Content.IsNone
                            | PartValue.Reminder value -> value.Plan.IsNone
                            | _ -> false))

                if missing && not erased then
                    Error RecordViolation.InvalidSubjectState
                else
                    Ok(changes, index)

    /// Restores exact accepted versions for live decisions; history succession is checked by apply.
    let private restoreParts
        financialCheck
        rowsCheck
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
                        | PartKind.Party ->
                            let caseOrigin = Map.tryFind (PartKind.Case, CaseId.value id) origins

                            caseOrigin
                            |> Option.exists (fun stamp ->
                                partyId stamp origin |> Option.map PartyId.value =
                                    Some change.PartId)
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
                            Some(reminder, accepted change value)
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

                let parties =
                    parts
                    |> List.choose (fun change ->
                        match change.Value, PartyId.create change.PartId with
                        | PartValue.Party value, Some party -> Some(party, accepted change value)
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
                        PartiesValue = parties
                        PartyOrigins =
                            parties
                            |> Map.map (fun party _ ->
                                (Map.find (PartKind.Party, PartyId.value party) origins).Position)
                        SequenceValue = sequence
                    }

                financialCheck payments value
                |> Result.bind (fun () -> rowsCheck state parts |> Result.map (fun () -> state))
            | _ -> invalid

    let apply state change = applyWith financial state change

    /// Full reconstruction reconciles every active payment and derives all child projections.
    let restore id reference sequence parts origins =
        restoreParts
            financial
            (fun state acquired -> rows state acquired |> Result.map ignore)
            id
            reference
            sequence
            parts
            origins

    /// The selected-parts constructor is internal; DecisionFacts adds its explicit aggregate facts.
    let restoreSelected id reference sequence parts origins =
        restoreParts (fun _ _ -> Ok()) (fun _ _ -> Ok()) id reference sequence parts origins

module CaseFamily =
    let validateSnapshot = FamilyParts.validateSnapshot

    let apply state change =
        FamilyParts.apply (state |> Option.map (fun (value: CaseFamily) -> value.Value)) change
        |> Result.map CaseFamily

    let snapshot (state: CaseFamily) = FamilyParts.snapshot state.Value
    let index (state: CaseFamily) = FamilyParts.index state.Value
    let rows (state: CaseFamily) changes = FamilyParts.rows state.Value changes

    let restore id reference sequence parts origins =
        FamilyParts.restore id reference sequence parts origins |> Result.map CaseFamily
