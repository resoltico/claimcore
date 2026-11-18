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

    let ofFinancial (position: FinancialPosition) =
        match FinancialPosition.totals position with
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

    let completesPayment =
        function
        | CaseProgress.Paid
        | CaseProgress.Overpaid -> true
        | _ -> false

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
                    |> Option.bind (fun (dates, received) -> CaseReportDates.value dates received field)
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
            | CaseAttention.DeadlineOverdue -> WorkSelection.Fact(WorkPredicate.NextDeadlineDue ReminderDue.Overdue)
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

        WorkSelection.All [ WorkSelection.activeOpen; attention ]

    let at today facts =
        all
        |> List.filter (fun reason -> WorkSelection.evaluate today None facts None (plan reason))

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
    | Paid
    | PartlyPaid
    | Decided
    | Undecided

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseHeadline =
    let vocabulary =
        Vocabulary.ofUnion<CaseHeadline> "headline" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    let ofState =
        function
        | CaseState.Erased -> CaseHeadline.Erased
        | CaseState.Voided _ -> CaseHeadline.Voided
        | CaseState.Active(CaseStatus.Closed, _) -> CaseHeadline.Closed
        | CaseState.Active(CaseStatus.Opened, (CaseProgress.Paid | CaseProgress.Overpaid)) ->
            CaseHeadline.Paid
        | CaseState.Active(CaseStatus.Opened, CaseProgress.PartlyPaid) -> CaseHeadline.PartlyPaid
        | CaseState.Active(CaseStatus.Opened, CaseProgress.Decided) -> CaseHeadline.Decided
        | CaseState.Active(CaseStatus.Opened, CaseProgress.Undecided) -> CaseHeadline.Undecided

/// Recommendations rank only the offered business acts on an active, open case.
