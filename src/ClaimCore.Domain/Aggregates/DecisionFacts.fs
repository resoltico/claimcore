namespace ClaimCore.Domain.Aggregates

open System
open ClaimCore.Domain
open ClaimCore.Domain.Changes

/// Projection aggregates are consumed facts, not proofs that every historical child was inspected.
type LiveFamilySummary =
    {
        PaymentCount: int64
        Paid: Amount
        PaymentDates: PaymentDateBounds option
        RemainingPaymentDates: (OperationId * PaymentDateBounds option) option
        NextPendingDue: CalendarDate option
        RemainingPendingDue: CalendarDate option
    }

/// Requested point reads distinguish a known-absent child from a child never acquired.
type FamilyFactScope =
    {
        Payments: Set<OperationId>
        Reminders: Set<ReminderId>
        Notes: Set<NoteId>
        Operation: OperationId option
    }

type private FamilyFactCoverage =
    | Selected of FamilyFactScope
    | Replay of CaseFamily

/// The current roots and selected children required for one decision/availability acquisition.
/// This type makes no complete-child-set guarantee and cannot be used as a replay CaseFamily.
type DecisionFacts =
    private
        {
            Parts: FamilyParts
            Summary: LiveFamilySummary
            Coverage: FamilyFactCoverage
        }

    member internal this.Value = this.Parts
    member this.Id = this.Parts.Id
    member this.Reference = this.Parts.Reference
    member this.Case = this.Parts.Case
    member this.Payments = this.Parts.Payments
    member this.Work = this.Parts.Work
    member this.Reminders = this.Parts.Reminders
    member this.Notes = this.Parts.Notes
    member this.Sequence = this.Parts.Sequence
    member this.ChangedAt = this.Parts.ChangedAt
    member this.NextPendingDue = this.Summary.NextPendingDue

module DecisionFacts =
    let private paymentBounds entries =
        match entries |> Seq.map PaymentEntry.date |> Seq.sort |> Seq.toList with
        | [] -> None
        | first :: rest ->
            PaymentDateBounds.create first (List.tryLast rest |> Option.defaultValue first)

    let private minimum values = values |> Seq.sort |> Seq.tryHead

    let private due (parts: FamilyParts) =
        parts.Reminders
        |> Map.values
        |> Seq.choose (fun part ->
            if part.Value.State = ReminderState.Pending then
                (part.Value.Plan |> Option.map _.Due)
            else
                None)

    let private nextDue (parts: FamilyParts) summary =
        if parts.Case.Value.Lifecycle = Lifecycle.Erased then
            None
        else
            Seq.append (summary.RemainingPendingDue |> Option.toList) (due parts) |> minimum

    let private validate (parts: FamilyParts) summary changes =
        let erased = parts.Case.Value.Lifecycle = Lifecycle.Erased

        let financial =
            match parts.Case.Value.Content with
            | None ->
                erased
                && summary.PaymentCount = 0L
                && Amount.value summary.Paid = 0M
                && summary.PaymentDates.IsNone
            | Some content ->
                content.Finance.PaymentCount = summary.PaymentCount
                && content.Finance.PaidAmount = summary.Paid
                && content.Finance.PaymentDates = summary.PaymentDates

        let selectedPrivate =
            (if erased then
                 changes
                 |> List.forall (fun change -> PartValue.clear change.Value = change.Value)
             else
                 ((parts.Work |> Option.forall (fun part -> part.Value.IsSome))
                  && (parts.Payments |> Map.forall (fun _ part -> part.Value.Entry.IsSome))
                  && (parts.Reminders
                      |> Map.forall (fun _ part -> (part.Value.Plan |> Option.map _.Due).IsSome))
                  && (changes
                      |> List.forall (fun change ->
                          match change.Value with
                          | PartValue.Note value -> value.Removed || value.Text.IsSome
                          | _ -> true))))

        if
            not financial
            || not selectedPrivate
            || nextDue parts summary <> summary.NextPendingDue
        then
            Error RecordViolation.InvalidSubjectState
        else
            Ok()

    let restore id reference sequence parts origins summary (scope: FamilyFactScope) =
        FamilyParts.restoreSelected id reference sequence parts origins
        |> Result.bind (fun current ->
            let scoped =
                parts
                |> List.forall (fun part ->
                    match part.Kind with
                    | PartKind.Payment ->
                        OperationId.create part.PartId
                        |> Option.exists (fun id -> Set.contains id scope.Payments)
                    | PartKind.Reminder ->
                        ReminderId.create part.PartId
                        |> Option.exists (fun id -> Set.contains id scope.Reminders)
                    | PartKind.Note ->
                        NoteId.create part.PartId
                        |> Option.exists (fun id -> Set.contains id scope.Notes)
                    | PartKind.Case
                    | PartKind.Work -> true
                    | PartKind.Person -> false)

            let correlated =
                summary.RemainingPaymentDates
                |> Option.forall (fun (id, _) -> Set.contains id scope.Payments)

            if not scoped || not correlated then
                Error RecordViolation.InvalidSubjectState
            else
                validate current summary parts
                |> Result.map (fun () ->
                    {
                        Parts = current
                        Summary = summary
                        Coverage = Selected scope
                    }))

    /// The complete replay computes its own aggregates; ordinary storage never takes this path.
    let fromReplay (family: CaseFamily) =
        let parts = family.Value

        let finance = family.Case.Value.Content |> Option.map _.Finance


        {
            Parts = parts
            Coverage = Replay family
            Summary =
                {
                    PaymentCount = finance |> Option.map _.PaymentCount |> Option.defaultValue 0L
                    Paid =
                        finance
                        |> Option.map _.PaidAmount
                        |> Option.defaultWith (fun () ->
                            Amount.parse "0" |> Result.toOption |> Option.get)
                    PaymentDates = finance |> Option.bind _.PaymentDates
                    RemainingPaymentDates = None
                    NextPendingDue = due parts |> minimum
                    RemainingPendingDue = None
                }
        }

    let remainingPaymentDates selected (facts: DecisionFacts) =
        match facts.Summary.RemainingPaymentDates, facts.Coverage with
        | Some(id, dates), _ when id = selected -> Ok dates
        | _, Replay complete ->
            complete.Payments
            |> Map.remove selected
            |> Map.values
            |> Seq.filter (fun part -> not part.Value.Removed)
            |> Seq.choose _.Value.Entry
            |> paymentBounds
            |> Ok
        | _ -> Error RecordViolation.InvalidPaymentState

    let requireSelection binding (facts: DecisionFacts) =
        let covered =
            match facts.Coverage with
            | Replay _ -> true
            | Selected scope ->
                match binding with
                | Binding.CasePayment(_, _, id) -> Set.contains id scope.Payments
                | Binding.Reminder(_, id, _) -> Set.contains id scope.Reminders
                | Binding.Note(_, id) -> Set.contains id scope.Notes
                | _ -> true

        if not covered then
            invalidArg "binding" "The decision facts did not acquire this child selection."

    let requireOperation operation (facts: DecisionFacts) =
        match facts.Coverage with
        | Replay _ -> ()
        | Selected scope when scope.Operation = Some operation -> ()
        | Selected _ ->
            invalidArg "operation" "Decision facts must be acquired for the authoring operation."

    let snapshot (facts: DecisionFacts) = FamilyParts.snapshot facts.Parts

    let private updatedSummary (parts: FamilyParts) (summary: LiveFamilySummary) =
        let finance = parts.Case.Value.Content |> Option.map _.Finance

        { summary with
            PaymentCount = finance |> Option.map _.PaymentCount |> Option.defaultValue 0L
            Paid =
                finance
                |> Option.map _.PaidAmount
                |> Option.defaultWith (fun () -> Amount.parse "0" |> Result.toOption |> Option.get)
            PaymentDates = finance |> Option.bind _.PaymentDates
            NextPendingDue = nextDue parts summary
        }

    /// Apply only the acquired parts; complete replay independently reconciles the full family.
    let apply (before: DecisionFacts option) changes =
        let check _ (value: CaseValue) =
            value.Content
            |> Option.map FamilyParts.validateContent
            |> Option.defaultValue (Ok())

        let initial = before |> Option.map _.Parts

        changes
        |> List.fold
            (fun result change ->
                result
                |> Result.bind (fun parts ->
                    FamilyParts.applyWith check parts change |> Result.map Some))
            (Ok initial)
        |> Result.bind (function
            | None -> Error RecordViolation.InvalidSubjectState
            | Some parts ->
                let summary =
                    match before with
                    | Some before -> before.Summary
                    | None ->
                        {
                            PaymentCount = 0L
                            Paid = Amount.parse "0" |> Result.toOption |> Option.get
                            PaymentDates = None
                            RemainingPaymentDates = None
                            NextPendingDue = None
                            RemainingPendingDue = None
                        }

                let after = updatedSummary parts summary
                let paymentDelta = changes |> List.choose PartChange.paymentDifference

                let countDelta =
                    paymentDelta
                    |> List.sumBy (function
                        | PaymentDifference.Recorded _ -> 1L
                        | PaymentDifference.Removed _ -> -1L)

                let paidDelta =
                    paymentDelta
                    |> List.sumBy (function
                        | PaymentDifference.Recorded entry ->
                            Amount.value (PaymentEntry.amount entry)
                        | PaymentDifference.Removed entry ->
                            -(Amount.value (PaymentEntry.amount entry)))

                if
                    parts.Case.Value.Lifecycle <> Lifecycle.Erased
                    && (after.PaymentCount <> summary.PaymentCount + countDelta
                        || Amount.value after.Paid <> Amount.value summary.Paid + paidDelta)
                then
                    Error RecordViolation.InvalidPaymentState
                else
                    validate parts after changes
                    |> Result.map (fun () ->
                        {
                            Parts = parts
                            Summary = after
                            Coverage =
                                match before |> Option.map _.Coverage with
                                | Some(Selected scope) -> Selected scope
                                | _ ->
                                    Selected
                                        {
                                            Payments = parts.Payments |> Map.keys |> Set.ofSeq
                                            Reminders = parts.Reminders |> Map.keys |> Set.ofSeq
                                            Notes = parts.Notes |> Map.keys |> Set.ofSeq
                                            Operation = None
                                        }
                        }))

    let rows (facts: DecisionFacts) changes =
        match snapshot facts with
        | None -> Error RecordViolation.InvalidSubjectState
        | Some snapshot ->
            validate facts.Parts facts.Summary changes
            |> Result.map (fun () ->
                let work =
                    facts.Work
                    |> Option.bind (fun part ->
                        part.Value
                        |> Option.map (fun value ->
                            {
                                CaseWorkSnapshot.CaseId = facts.Id
                                Revision = part.Revision
                                Work = value
                            }))

                let due = facts.NextPendingDue |> Option.toList
                changes, CaseIndex.create facts.ChangedAt snapshot work due)
