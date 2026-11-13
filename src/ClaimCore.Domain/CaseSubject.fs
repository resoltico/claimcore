namespace ClaimCore.Domain

open System

/// Accepted position and attribution belong to an effect, independently of its private content.
type SubjectStamp =
    {
        Position: EffectPosition
        OperationId: OperationId
        ActorId: ActorId
        RecordedAt: DateTimeOffset
    }

/// Retained case structure survives erasure; private values are never reconstructed from placeholders.
type CaseSubjectStructure =
    {
        Stamp: SubjectStamp
        CaseId: CaseId
        Reference: CaseReference option
        Revision: Revision
        Act: CaseActKind
        RemovedPayment: OperationId option
    }

type CaseSubjectState =
    {
        CaseId: CaseId
        Reference: CaseReference
        Revision: Revision
        Lifecycle: Lifecycle
        Status: CaseStatus
        Snapshot: CaseSnapshot option
        Payments: Map<OperationId, PaymentProjectionRow>
        ChangedAt: DateTimeOffset
    }

type CaseSubjectRows =
    {
        Snapshot: CaseSnapshot
        Payments: PaymentProjectionRow list
        ChangedAt: DateTimeOffset
    }

module CaseSubject =
    let active (state: CaseSubjectState) = CasePayments.active state.Payments

    let private cleared payments =
        payments
        |> Map.map (fun _ row ->
            { row with
                Content =
                    match row.Content with
                    | PaymentProjectionContent.Available(_, removed)
                    | PaymentProjectionContent.Cleared removed ->
                        PaymentProjectionContent.Cleared removed
            })

    /// Admission of stored state establishes that the owned payment set agrees with its financial facts.
    let restore
        changedAt
        (snapshot: CaseSnapshot)
        (payments: Map<OperationId, PaymentProjectionRow>)
        =
        let state =
            {
                CaseId = snapshot.CaseId
                Reference = snapshot.Reference
                Revision = snapshot.Revision
                Lifecycle = CaseState.lifecycle snapshot.State
                Status =
                    CaseState.fields snapshot.State
                    |> Option.map _.Status
                    |> Option.defaultValue CaseStatus.Opened
                Snapshot = Some snapshot
                Payments = payments
                ChangedAt = changedAt
            }

        let owned =
            payments
            |> Map.forall (fun id row ->
                row.CaseId = snapshot.CaseId
                && row.PaymentId = id
                && (match row.Content with
                    | PaymentProjectionContent.Available(entry, _) -> PaymentEntry.id entry = id
                    | PaymentProjectionContent.Cleared _ -> true))

        let complete =
            match CaseState.fields snapshot.State with
            | None ->
                payments
                |> Map.forall (fun _ row ->
                    match row.Content with
                    | PaymentProjectionContent.Cleared _ -> true
                    | _ -> false)
            | Some fields ->
                let entries = active state
                let total = entries |> Map.values |> Seq.sumBy (PaymentEntry.amount >> Amount.value)

                payments
                |> Map.forall (fun _ row ->
                    match row.Content with
                    | PaymentProjectionContent.Available _ -> true
                    | _ -> false)
                && fields.Finance.PaymentCount = int64 entries.Count
                && Amount.value fields.Finance.PaidAmount = total
                && fields.Finance.PaymentDates = CasePayments.dates entries

        if not owned || not complete then
            Error RecordViolation.InvalidPaymentState
        else
            Case.ofAccepted snapshot |> Result.map (fun _ -> state)

    let decide today (state: CaseSubjectState option) (command: CaseCommand) =
        let current = state |> Option.bind _.Snapshot
        let payments = state |> Option.map _.Payments |> Option.defaultValue Map.empty

        if state.IsSome && current.IsNone then
            Error(CaseDecisionError.InvalidState RecordViolation.InvalidFinancialPosition)
        else
            Case.decideSnapshot today payments command current

    let apply (stamp: SubjectStamp) (state: CaseSubjectState option) (effect: CaseDecision) =
        let snapshot = effect.After
        let payments = state |> Option.map _.Payments |> Option.defaultValue Map.empty

        let payments =
            match snapshot.State, effect.Payment with
            | CaseState.Erased, _ -> cleared payments
            | _, Some(PaymentDifference.Recorded entry) ->
                Map.add
                    (PaymentEntry.id entry)
                    {
                        CaseId = snapshot.CaseId
                        PaymentId = PaymentEntry.id entry
                        Position = stamp.Position
                        Content = PaymentProjectionContent.Available(entry, false)
                    }
                    payments
            | _, Some(PaymentDifference.Removed entry) ->
                let row = Map.find (PaymentEntry.id entry) payments

                Map.add
                    row.PaymentId
                    { row with
                        Content = PaymentProjectionContent.Available(entry, true)
                    }
                    payments
            | _ -> payments

        Some
            {
                CaseId = snapshot.CaseId
                Reference = snapshot.Reference
                Revision = snapshot.Revision
                Lifecycle = CaseState.lifecycle snapshot.State
                Status =
                    CaseState.fields snapshot.State
                    |> Option.map _.Status
                    |> Option.defaultValue CaseStatus.Opened
                Snapshot = Some snapshot
                Payments = payments
                ChangedAt = stamp.RecordedAt
            }

    let private structure (state: CaseSubjectState option) (effect: CaseSubjectStructure) =
        let priorRevision =
            state |> Option.map _.Revision |> Option.defaultValue Revision.zero

        let priorLifecycle =
            state |> Option.map _.Lifecycle |> Option.defaultValue Lifecycle.Active

        let reference = state |> Option.map _.Reference |> Option.orElse effect.Reference
        let nextLifecycle = Lifecycle.after priorLifecycle effect.Act

        let validStart =
            state.IsNone = (effect.Act = CaseActKind.Open)
            && (if effect.Act = CaseActKind.Open then
                    effect.CaseId = CaseId.ofOperationId effect.Stamp.OperationId
                    && effect.Reference.IsSome
                else
                    effect.Reference.IsNone)

        if
            Revision.value priorRevision = Revision.Maximum
            || effect.Revision <> Revision.successor priorRevision
        then
            Error RecordViolation.InvalidRevisionSequence
        elif not validStart || reference.IsNone then
            Error RecordViolation.InvalidHistoryStart
        elif
            nextLifecycle.IsNone
            || (state |> Option.exists (fun state -> state.CaseId <> effect.CaseId))
        then
            Error RecordViolation.InvalidSubjectState
        else
            Ok(reference.Value, nextLifecycle.Value)

    let validateStructure state effect =
        structure state effect |> Result.map ignore

    let applyErased (state: CaseSubjectState option) (effect: CaseSubjectStructure) =
        match structure state effect with
        | Error error -> Error error
        | Ok(reference, lifecycle) ->
            let payments = state |> Option.map _.Payments |> Option.defaultValue Map.empty

            let payments =
                match effect.Act, effect.RemovedPayment with
                | CaseActKind.RecordPayment, None when
                    not (Map.containsKey effect.Stamp.OperationId payments)
                    ->
                    Ok(
                        Map.add
                            effect.Stamp.OperationId
                            {
                                CaseId = effect.CaseId
                                PaymentId = effect.Stamp.OperationId
                                Position = effect.Stamp.Position
                                Content = PaymentProjectionContent.Cleared false
                            }
                            payments
                    )
                | CaseActKind.RemovePayment, Some id ->
                    match Map.tryFind id payments with
                    | Some row ->
                        match row.Content with
                        | PaymentProjectionContent.Available(_, false)
                        | PaymentProjectionContent.Cleared false ->
                            Ok(
                                Map.add
                                    id
                                    { row with
                                        Content = PaymentProjectionContent.Cleared true
                                    }
                                    payments
                            )
                        | _ -> Error RecordViolation.InvalidPaymentState
                    | None -> Error RecordViolation.InvalidPaymentState
                | CaseActKind.RecordPayment, _
                | CaseActKind.RemovePayment, _ -> Error RecordViolation.InvalidPaymentState
                | CaseActKind.Erase, None -> Ok(cleared payments)
                | _, None -> Ok payments
                | _ -> Error RecordViolation.InvalidPaymentState

            payments
            |> Result.map (fun payments ->
                let snapshot =
                    if lifecycle = Lifecycle.Erased then
                        Some
                            {
                                CaseId = effect.CaseId
                                Reference = reference
                                Revision = effect.Revision
                                State = CaseState.Erased
                            }
                    else
                        None

                Some
                    {
                        CaseId = effect.CaseId
                        Reference = reference
                        Revision = effect.Revision
                        Lifecycle = lifecycle
                        Status =
                            match effect.Act with
                            | CaseActKind.Open
                            | CaseActKind.Reopen -> CaseStatus.Opened
                            | CaseActKind.Close -> CaseStatus.Closed
                            | _ ->
                                state
                                |> Option.map _.Status
                                |> Option.defaultValue CaseStatus.Opened
                        Snapshot = snapshot
                        Payments = payments
                        ChangedAt = effect.Stamp.RecordedAt
                    })

    let rows (state: CaseSubjectState) =
        let snapshot =
            match state.Snapshot, state.Lifecycle with
            | Some snapshot, _ when
                snapshot.CaseId = state.CaseId
                && snapshot.Reference = state.Reference
                && snapshot.Revision = state.Revision
                && CaseState.lifecycle snapshot.State = state.Lifecycle
                ->
                Some snapshot
            | Some _, _ -> None
            | None, Lifecycle.Erased ->
                Some
                    {
                        CaseId = state.CaseId
                        Reference = state.Reference
                        Revision = state.Revision
                        State = CaseState.Erased
                    }
            | None, _ -> None

        snapshot
        |> Option.map (fun snapshot ->
            {
                Snapshot = snapshot
                Payments = state.Payments |> Map.values |> Seq.toList
                ChangedAt = state.ChangedAt
            })
