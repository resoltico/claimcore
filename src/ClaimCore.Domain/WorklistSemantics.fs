namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type CaseProgress =
    | Undecided
    | Decided
    | PartlyPaid
    | Paid
    | Overpaid

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseProgress =
    let vocabulary =
        Vocabulary.ofUnion<CaseProgress> "progress" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

    let ofComparison comparison =
        match comparison with
        | None -> CaseProgress.Undecided
        | Some totals ->
            let payable = DecimalTotal.ofAmount (Money.amount totals.Payable)

            if totals.NetPaid > payable then
                CaseProgress.Overpaid
            elif totals.NetPaid = payable then
                CaseProgress.Paid
            elif totals.NetPaid > DecimalTotal.zero then
                CaseProgress.PartlyPaid
            else
                CaseProgress.Decided

    let ofFinancial position =
        FinancialPosition.comparePayable position |> ofComparison

    let completesPayment =
        function
        | CaseProgress.Paid
        | CaseProgress.Overpaid -> true
        | _ -> false

type FinancialAssessment =
    {
        Comparison: PayableComparison option
        Progress: CaseProgress
        Overpayment: (Currency * DecimalTotal) option
        NegativeBalances: Map<Currency, DecimalTotal>
        SettledWithoutCoverage: bool
    }

    member this.NeedsReview =
        this.Overpayment.IsSome
        || not this.NegativeBalances.IsEmpty
        || this.SettledWithoutCoverage

module FinancialAssessment =
    let ofPosition outcome position =
        let comparison = FinancialPosition.comparePayable position
        let progress = CaseProgress.ofComparison comparison

        let overpayment =
            comparison
            |> Option.bind (fun value ->
                let payable = DecimalTotal.ofAmount (Money.amount value.Payable)

                if value.NetPaid > payable then
                    let excess =
                        DecimalTotal.sum [ value.NetPaid; DecimalTotal.negate payable ]
                        |> Result.defaultWith (fun _ ->
                            invalidOp "A positive excess cannot exceed net paid.")

                    Some(Money.currencyValue value.Payable, excess)
                else
                    None)

        {
            Comparison = comparison
            Progress = progress
            Overpayment = overpayment
            NegativeBalances =
                FinancialPosition.actualBalances position
                |> Map.filter (fun _ net -> net < DecimalTotal.zero)
            SettledWithoutCoverage =
                outcome = Some ClosureOutcome.Settled
                && not (
                    CaseProgress.completesPayment progress
                    && comparison |> Option.exists (fun value -> Money.value value.Payable > 0M)
                )
        }

    let ofCase (fields: CaseFields) =
        ofPosition (Handling.closure fields.Handling |> Option.map _.Outcome) fields.Finance

/// Core facts needed to classify a case in a worklist. Availability is resolved by the core read.
type CaseWorkFacts =
    {
        State: CaseState<CaseStatus * CaseProgress>
        Responsible: (ActorId * bool) option
        NextPendingDue: CalendarDate option
        PaymentsNeedReview: bool
        NextDeadlineDue: CalendarDate option
        ReportDates: (CaseReportDates * CalendarDate option) option
    }

[<RequireQualifiedAccess>]
type CaseView =
    | NeedsAttention
    | Mine
    | Overdue
    | DueToday
    | NoNextAction
    | Unassigned
    | Undecided
    | DecidedUnpaid
    | Closed
    | Voided
    | All

[<RequireQualifiedAccess>]
type ReminderView =
    | Pending
    | Mine
    | Overdue
    | Today
    | Upcoming
    | Completed
    | Cancelled

[<RequireQualifiedAccess>]
type CaseAttention =
    | DeadlineOverdue
    | Overdue
    | DueToday
    | NoNextAction
    | Unassigned
    | AssigneeUnavailable
    | PaymentsNeedReview
    | ReadyToClose

/// Closed work predicates. Absence never satisfies a value/date comparison.
[<RequireQualifiedAccess>]
type WorkPredicate =
    | Lifecycle of Lifecycle
    | Status of CaseStatus
    | Progress of CaseProgress
    | Mine
    | Responsible of ActorId option
    | Unassigned
    | AssigneeUnavailable
    | NoNextDue
    | PaymentsNeedReview
    | NextDue of ReminderDue
    | NextDeadlineDue of ReminderDue
    | ReminderState of ReminderState
    | ReminderDue of ReminderDue
    | CaseDate of CaseDateFilter * CalendarDate option

[<RequireQualifiedAccess>]
type WorkSelection =
    | All of WorkSelection list
    | Any of WorkSelection list
    | Not of WorkSelection
    | Fact of WorkPredicate

module WorkSelection =
    let activeOpen =
        let lifecycle, status = CaseState.actionable

        WorkSelection.All
            [
                WorkSelection.Fact(WorkPredicate.Lifecycle lifecycle)
                WorkSelection.Fact(WorkPredicate.Status status)
            ]

    let rec interpret fact =
        function
        | WorkSelection.All selections -> selections |> List.forall (interpret fact)
        | WorkSelection.Any selections -> selections |> List.exists (interpret fact)
        | WorkSelection.Not selection -> not (interpret fact selection)
        | WorkSelection.Fact predicate -> fact predicate

    /// Timing classifiers consume only facts in their declared timing plan.
    let timing today lifecycle status (nextDue: CalendarDate option) =
        interpret (function
            | WorkPredicate.Lifecycle value -> lifecycle = value
            | WorkPredicate.Status value -> status = Some value
            | WorkPredicate.NoNextDue -> nextDue.IsNone
            | WorkPredicate.NextDue value ->
                nextDue |> Option.exists (ReminderDue.matchesDate today value)
            | predicate -> invalidArg (nameof predicate) "Not a case timing fact.")

    let evaluate
        today
        requester
        (facts: CaseWorkFacts)
        (reminder: (ReminderState * CalendarDate) option)
        =
        interpret (function
            | WorkPredicate.Lifecycle value -> CaseState.lifecycle facts.State = value
            | WorkPredicate.Status value ->
                CaseState.fields facts.State
                |> Option.exists (fun (status, _) -> status = value)
            | WorkPredicate.Progress value ->
                CaseState.fields facts.State
                |> Option.exists (fun (_, progress) -> progress = value)
            | WorkPredicate.CaseDate(filter, bound) ->
                let field, _ = CaseDateFilter.bound filter

                let date =
                    facts.ReportDates
                    |> Option.bind (fun (dates, received) ->
                        CaseReportDates.value dates received field)

                CaseDateFilter.matches filter bound date
            | WorkPredicate.Mine ->
                facts.Responsible |> Option.exists (fun (actor, _) -> Some actor = requester)
            | WorkPredicate.Responsible value ->
                value
                |> Option.forall (fun expected ->
                    facts.Responsible |> Option.exists (fun (actor, _) -> actor = expected))
            | WorkPredicate.Unassigned -> facts.Responsible.IsNone
            | WorkPredicate.AssigneeUnavailable ->
                facts.Responsible |> Option.exists (fun (_, available) -> not available)
            | WorkPredicate.PaymentsNeedReview -> facts.PaymentsNeedReview
            | WorkPredicate.NoNextDue -> facts.NextPendingDue.IsNone
            | WorkPredicate.NextDue value ->
                facts.NextPendingDue |> Option.exists (ReminderDue.matchesDate today value)
            | WorkPredicate.NextDeadlineDue value ->
                facts.NextDeadlineDue |> Option.exists (ReminderDue.matchesDate today value)
            | WorkPredicate.ReminderState value ->
                reminder |> Option.exists (fun (state, _) -> state = value)
            | WorkPredicate.ReminderDue value ->
                reminder
                |> Option.exists (fun (_, due) -> ReminderDue.matchesDate today value due))

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseAttention =
    let vocabulary =
        Vocabulary.ofUnion<CaseAttention> "attention" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    let plan reason =
        let attention =
            match reason with
            | CaseAttention.DeadlineOverdue ->
                WorkSelection.Fact(WorkPredicate.NextDeadlineDue ReminderDue.Overdue)
            | CaseAttention.Overdue -> WorkSelection.Fact(WorkPredicate.NextDue ReminderDue.Overdue)
            | CaseAttention.DueToday -> WorkSelection.Fact(WorkPredicate.NextDue ReminderDue.Today)
            | CaseAttention.NoNextAction -> WorkSelection.Fact WorkPredicate.NoNextDue
            | CaseAttention.Unassigned -> WorkSelection.Fact WorkPredicate.Unassigned
            | CaseAttention.AssigneeUnavailable ->
                WorkSelection.Fact WorkPredicate.AssigneeUnavailable
            | CaseAttention.PaymentsNeedReview ->
                WorkSelection.Fact WorkPredicate.PaymentsNeedReview
            | CaseAttention.ReadyToClose ->
                Vocabulary.all CaseProgress.vocabulary
                |> List.filter CaseProgress.completesPayment
                |> List.map (WorkPredicate.Progress >> WorkSelection.Fact)
                |> WorkSelection.Any

        match reason with
        | CaseAttention.PaymentsNeedReview ->
            WorkSelection.All
                [ WorkSelection.Fact(WorkPredicate.Lifecycle Lifecycle.Active); attention ]
        | CaseAttention.ReadyToClose ->
            WorkSelection.All
                [
                    WorkSelection.activeOpen
                    attention
                    WorkSelection.Not(WorkSelection.Fact WorkPredicate.PaymentsNeedReview)
                ]
        | _ -> WorkSelection.All [ WorkSelection.activeOpen; attention ]

    let subsumedBy =
        function
        | CaseAttention.Overdue -> Some CaseAttention.DeadlineOverdue
        | _ -> None

    let at today facts =
        let matching =
            all
            |> List.filter (fun reason ->
                WorkSelection.evaluate today None facts None (plan reason))

        matching
        |> List.filter (fun reason ->
            subsumedBy reason
            |> Option.forall (fun stronger -> not (List.contains stronger matching)))

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseView =
    let vocabulary =
        Vocabulary.ofUnion<CaseView> "caseView" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    let plan =
        function
        | CaseView.All -> WorkSelection.All []
        | CaseView.Mine -> WorkSelection.Fact WorkPredicate.Mine
        | CaseView.NeedsAttention ->
            WorkSelection.Any(CaseAttention.all |> List.map CaseAttention.plan)
        | CaseView.Overdue -> CaseAttention.plan CaseAttention.Overdue
        | CaseView.DueToday -> CaseAttention.plan CaseAttention.DueToday
        | CaseView.NoNextAction -> CaseAttention.plan CaseAttention.NoNextAction
        | CaseView.Unassigned -> CaseAttention.plan CaseAttention.Unassigned
        | CaseView.Undecided ->
            WorkSelection.All
                [
                    WorkSelection.activeOpen
                    WorkSelection.Fact(WorkPredicate.Progress CaseProgress.Undecided)
                ]
        | CaseView.DecidedUnpaid ->
            WorkSelection.All
                [
                    WorkSelection.activeOpen
                    WorkSelection.Any
                        [
                            WorkSelection.Fact(WorkPredicate.Progress CaseProgress.Decided)
                            WorkSelection.Fact(WorkPredicate.Progress CaseProgress.PartlyPaid)
                        ]
                ]
        | CaseView.Closed -> WorkSelection.Fact(WorkPredicate.Status CaseStatus.Closed)
        | CaseView.Voided -> WorkSelection.Fact(WorkPredicate.Lifecycle Lifecycle.Voided)

    let matches today requester view facts =
        WorkSelection.evaluate today (Some requester) facts None (plan view)

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ReminderView =
    let vocabulary =
        Vocabulary.ofUnion<ReminderView> "reminderView" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    let duePlan due =
        WorkSelection.All
            [
                WorkSelection.activeOpen
                WorkSelection.Fact(WorkPredicate.ReminderState ReminderState.Pending)
                WorkSelection.Fact(WorkPredicate.ReminderDue due)
            ]

    let plan =
        function
        | ReminderView.Pending ->
            WorkSelection.All
                [
                    WorkSelection.activeOpen
                    WorkSelection.Fact(WorkPredicate.ReminderState ReminderState.Pending)
                ]
        | ReminderView.Mine ->
            WorkSelection.All
                [
                    WorkSelection.activeOpen
                    WorkSelection.Fact(WorkPredicate.ReminderState ReminderState.Pending)
                    WorkSelection.Fact WorkPredicate.Mine
                ]
        | ReminderView.Overdue -> duePlan ReminderDue.Overdue
        | ReminderView.Today -> duePlan ReminderDue.Today
        | ReminderView.Upcoming -> duePlan ReminderDue.Upcoming
        | ReminderView.Completed ->
            WorkSelection.All
                [
                    WorkSelection.Not(WorkSelection.Fact(WorkPredicate.Lifecycle Lifecycle.Erased))
                    WorkSelection.Fact(WorkPredicate.ReminderState ReminderState.Completed)
                ]
        | ReminderView.Cancelled ->
            WorkSelection.All
                [
                    WorkSelection.Not(WorkSelection.Fact(WorkPredicate.Lifecycle Lifecycle.Erased))
                    WorkSelection.Fact(WorkPredicate.ReminderState ReminderState.Cancelled)
                ]

    let matches today requester view parent reminder =
        WorkSelection.evaluate
            today
            (Some requester)
            parent
            (Some(Reminder.state reminder, Reminder.due reminder))
            (plan view)

/// A case has one headline, independent of the renderer and of the fields it displays.
[<RequireQualifiedAccess>]
type CaseHeadline =
    | Erased
    | Voided
    | Closed
    | NoPaymentDue
    | PayableCovered
    | Overpaid
    | PartlyPaid
    | Decided
    | Undecided

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseHeadline =
    let vocabulary =
        Vocabulary.ofUnion<CaseHeadline> "headline" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    let financial finance =
        let comparison = FinancialPosition.comparePayable finance

        match CaseProgress.ofComparison comparison with
        | CaseProgress.Paid when comparison |> Option.exists PayableComparison.noPaymentDue ->
            CaseHeadline.NoPaymentDue
        | CaseProgress.Paid -> CaseHeadline.PayableCovered
        | CaseProgress.Overpaid -> CaseHeadline.Overpaid
        | CaseProgress.PartlyPaid -> CaseHeadline.PartlyPaid
        | CaseProgress.Decided -> CaseHeadline.Decided
        | CaseProgress.Undecided -> CaseHeadline.Undecided

    let ofState =
        function
        | CaseState.Erased -> CaseHeadline.Erased
        | CaseState.Voided _ -> CaseHeadline.Voided
        | CaseState.Active(CaseStatus.Closed, _) -> CaseHeadline.Closed
        | CaseState.Active(CaseStatus.Opened, finance) -> financial finance

    let ofCase state =
        state
        |> CaseState.map (fun (fields: CaseFields) -> fields.Status, fields.Finance)
        |> ofState
