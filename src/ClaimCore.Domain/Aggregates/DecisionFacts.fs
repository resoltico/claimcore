namespace ClaimCore.Domain.Aggregates

open System
open ClaimCore.Domain
open ClaimCore.Domain.Changes

/// Projection aggregates are consumed facts, not proofs that every historical child was inspected.
type LiveFamilySummary =
    {
        PaymentCount: int64
        PaymentTotals: Map<Currency, CurrencyPaymentTotals>
        NextPendingDue: CalendarDate option
        RemainingPendingDue: CalendarDate option
        NextDeadlineDue: CalendarDate option
        RemainingDeadlineDue: CalendarDate option
    }

/// Requested point reads distinguish a known-absent child from a child never acquired.
type FamilyFactScope =
    {
        Payments: Set<OperationId>
        Reminders: Set<ReminderId>
        Notes: Set<NoteId>
        Attachments: Set<AttachmentId>
        Parties: Set<PartyId>
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
    member this.Attachments = this.Parts.Attachments
    member this.Parties = this.Parts.Parties
    member this.Links = this.Parts.Links
    member this.Sequence = this.Parts.Sequence
    member this.ChangedAt = this.Parts.ChangedAt
    member this.NextPendingDue = this.Summary.NextPendingDue

module DecisionFacts =
    let private minimum values = values |> Seq.sort |> Seq.tryHead

    let private due (parts: FamilyParts) kind =
        parts.Reminders
        |> Map.values
        |> Seq.choose (fun part -> ReminderValue.pendingPlan part.Value)
        |> Seq.filter (fun plan -> kind |> Option.forall ((=) plan.Kind))
        |> Seq.map _.Due

    let private nextDue (parts: FamilyParts) kind remaining =
        if parts.Case.Value.Lifecycle = Lifecycle.Erased then
            None
        else
            Seq.append (remaining |> Option.toList) (due parts kind) |> minimum

    let private nextDeadline parts remaining =
        nextDue parts (Some ReminderKind.Deadline) remaining

    let private validate (parts: FamilyParts) summary changes =
        let erased = parts.Case.Value.Lifecycle = Lifecycle.Erased

        let financial =
            match parts.Case.Value.Content with
            | None -> erased && summary.PaymentCount = 0L && summary.PaymentTotals.IsEmpty
            | Some content ->
                content.Finance.PaymentCount = summary.PaymentCount
                && content.Finance.CurrencyTotals = summary.PaymentTotals

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
                          | PartValue.Attachment(AttachmentEffect.Added(_, None)) -> false
                          | PartValue.Party value -> value.Content.IsSome
                          | PartValue.Link value -> value.Removed <> value.Content.IsSome
                          | _ -> true))))

        if
            not financial
            || not selectedPrivate
            || nextDue parts None summary.RemainingPendingDue <> summary.NextPendingDue
            || nextDeadline parts summary.RemainingDeadlineDue <> summary.NextDeadlineDue
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
                    | PartKind.Attachment ->
                        AttachmentId.create part.PartId
                        |> Option.exists (fun id -> Set.contains id scope.Attachments)
                    | PartKind.Note ->
                        NoteId.create part.PartId
                        |> Option.exists (fun id -> Set.contains id scope.Notes)
                    | PartKind.Case
                    | PartKind.Work
                    | PartKind.Party
                    | PartKind.Link -> true
                    | PartKind.Person -> false)

            if not scoped then
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
                    PaymentTotals =
                        finance |> Option.map _.CurrencyTotals |> Option.defaultValue Map.empty
                    NextPendingDue = due parts None |> minimum
                    RemainingPendingDue = None
                    NextDeadlineDue = nextDeadline parts None
                    RemainingDeadlineDue = None
                }
        }

    let requireSelection binding (facts: DecisionFacts) =
        let covered =
            match facts.Coverage with
            | Replay _ -> true
            | Selected scope ->
                match binding with
                | Binding.CasePayment(_, _, id) -> Set.contains id scope.Payments
                | Binding.Reminder(_, id, _) -> Set.contains id scope.Reminders
                | Binding.Note(_, id) -> Set.contains id scope.Notes
                | Binding.Attachment(_, id) -> Set.contains id scope.Attachments
                | Binding.Party(_, id, _) ->
                    Set.contains id scope.Parties
                    || (Map.tryFind id facts.Parts.Parties
                        |> Option.exists (fun party -> not party.Value.Removed))
                | _ -> true

        if not covered then
            invalidArg "binding" "The decision facts did not acquire this child selection."

    let requireOperation operation (facts: DecisionFacts) =
        match facts.Coverage with
        | Replay _ -> ()
        | Selected scope when scope.Operation = Some operation -> ()
        | Selected _ ->
            invalidArg "operation" "Decision facts must be acquired for the authoring operation."

    let currentParties (facts: DecisionFacts) = FamilyParts.currentParties facts.Parts

    let snapshot (facts: DecisionFacts) = FamilyParts.snapshot facts.Parts

    let private updatedSummary (parts: FamilyParts) (summary: LiveFamilySummary) =
        let finance = parts.Case.Value.Content |> Option.map _.Finance

        { summary with
            PaymentCount = finance |> Option.map _.PaymentCount |> Option.defaultValue 0L
            PaymentTotals = finance |> Option.map _.CurrencyTotals |> Option.defaultValue Map.empty
            NextPendingDue = nextDue parts None summary.RemainingPendingDue
            NextDeadlineDue = nextDeadline parts summary.RemainingDeadlineDue
        }

    /// Apply only the acquired parts; complete replay independently reconciles the full family.
    let apply (before: DecisionFacts option) changes =
        let initial = before |> Option.map _.Parts

        changes
        |> List.fold
            (fun result change ->
                result
                |> Result.bind (fun parts ->
                    FamilyParts.applyWith (fun _ _ -> Ok()) parts change |> Result.map Some))
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
                            PaymentTotals = Map.empty
                            NextPendingDue = None
                            RemainingPendingDue = None
                            NextDeadlineDue = None
                            RemainingDeadlineDue = None
                        }

                let after = updatedSummary parts summary
                let paymentDelta = changes |> List.choose PartChange.paymentDifference

                let expected =
                    let initial =
                        before
                        |> Option.bind (fun facts -> facts.Case.Value.Content)
                        |> Option.map _.Finance
                        |> Option.defaultValue FinancialPosition.undecided

                    (Ok initial, paymentDelta)
                    ||> List.fold (fun previous change ->
                        previous
                        |> Result.bind (
                            match change with
                            | PaymentDifference.Recorded entry -> FinancialPosition.add entry
                            | PaymentDifference.Removed entry -> FinancialPosition.remove entry
                        ))

                let matches =
                    match expected with
                    | Ok expected ->
                        after.PaymentCount = expected.PaymentCount
                        && after.PaymentTotals = expected.CurrencyTotals
                    | Error _ -> false

                if parts.Case.Value.Lifecycle <> Lifecycle.Erased && not matches then
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
                                            Attachments =
                                                parts.Attachments |> Map.keys |> Set.ofSeq
                                            Parties = parts.Parties |> Map.keys |> Set.ofSeq
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

                changes,
                CaseIndex.create
                    facts.ChangedAt
                    snapshot
                    work
                    facts.NextPendingDue
                    facts.Summary.NextDeadlineDue
                    (FamilyParts.currentParties facts.Parts))
