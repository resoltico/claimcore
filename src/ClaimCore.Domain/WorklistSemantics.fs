namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type CaseProgress =
    | Undecided
    | Decided
    | PartlyPaid
    | Paid

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseProgress =
    let vocabulary =
        Vocabulary.ofUnion<CaseProgress> "progress" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

    let ofFinancial (position: FinancialPosition) =
        if position.Decision.IsNone then
            CaseProgress.Undecided
        elif FinancialPosition.isPaid position then
            CaseProgress.Paid
        elif Amount.value position.PaidAmount = 0M then
            CaseProgress.Decided
        else
            CaseProgress.PartlyPaid

    let ofPayment =
        function
        | PaymentProgress.Undecided -> CaseProgress.Undecided
        | PaymentProgress.Decided _ -> CaseProgress.Decided
        | PaymentProgress.PartlyPaid _ -> CaseProgress.PartlyPaid
        | PaymentProgress.Paid _ -> CaseProgress.Paid


/// Core facts needed to classify a case in a worklist. Availability is resolved by the core read.
type CaseWorkFacts =
    {
        State: CaseState<CaseStatus * CaseProgress>
        Responsible: (ActorId * bool) option
        NextPendingDue: CalendarDate option
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
    | Overdue
    | DueToday
    | NoNextAction
    | Unassigned
    | AssigneeUnavailable
    | ReadyToClose

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseAttention =
    let vocabulary =
        Vocabulary.ofUnion<CaseAttention> "attention" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    let at today facts =
        match facts.State with
        | CaseState.Active(CaseStatus.Opened, progress) ->
            [
                match facts.NextPendingDue with
                | None -> CaseAttention.NoNextAction
                | Some due when CalendarDate.value due < today -> CaseAttention.Overdue
                | Some due when CalendarDate.value due = today -> CaseAttention.DueToday
                | Some _ -> ()
                match facts.Responsible with
                | None -> CaseAttention.Unassigned
                | Some(_, false) -> CaseAttention.AssigneeUnavailable
                | Some(_, true) -> ()
                if progress = CaseProgress.Paid then
                    CaseAttention.ReadyToClose
            ]
        | CaseState.Active(CaseStatus.Closed, _)
        | CaseState.Voided _
        | CaseState.Erased -> []

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseView =
    let vocabulary =
        Vocabulary.ofUnion<CaseView> "caseView" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    let matches today requester view facts =
        let attention reason =
            CaseAttention.at today facts |> List.contains reason

        match view with
        | CaseView.All -> true
        | CaseView.Mine -> facts.Responsible |> Option.exists (fun (actor, _) -> actor = requester)
        | CaseView.NeedsAttention -> CaseAttention.at today facts |> List.isEmpty |> not
        | CaseView.Overdue -> attention CaseAttention.Overdue
        | CaseView.DueToday -> attention CaseAttention.DueToday
        | CaseView.NoNextAction -> attention CaseAttention.NoNextAction
        | CaseView.Unassigned -> attention CaseAttention.Unassigned
        | CaseView.Undecided ->
            facts.State = CaseState.Active(CaseStatus.Opened, CaseProgress.Undecided)
        | CaseView.DecidedUnpaid ->
            (facts.State = CaseState.Active(CaseStatus.Opened, CaseProgress.Decided)
             || facts.State = CaseState.Active(CaseStatus.Opened, CaseProgress.PartlyPaid))
        | CaseView.Closed ->
            facts.State
            |> CaseState.fields
            |> Option.exists (fun (status, _) -> status = CaseStatus.Closed)
        | CaseView.Voided -> CaseState.lifecycle facts.State = Lifecycle.Voided

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ReminderView =
    let vocabulary =
        Vocabulary.ofUnion<ReminderView> "reminderView" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    let matches today requester view parent reminder =
        let readableParent = parent.State <> CaseState.Erased

        let actionable =
            match parent.State with
            | CaseState.Active(CaseStatus.Opened, _) ->
                Reminder.state reminder = ReminderState.Pending
            | _ -> false

        let timing = ReminderDue.at today actionable reminder

        match view with
        | ReminderView.Completed ->
            readableParent && Reminder.state reminder = ReminderState.Completed
        | ReminderView.Cancelled ->
            readableParent && Reminder.state reminder = ReminderState.Cancelled
        | ReminderView.Pending -> actionable
        | ReminderView.Mine ->
            actionable
            && (parent.Responsible |> Option.exists (fun (actor, _) -> actor = requester))
        | ReminderView.Overdue -> timing = Some ReminderDue.Overdue
        | ReminderView.Today -> timing = Some ReminderDue.Today
        | ReminderView.Upcoming -> timing = Some ReminderDue.Upcoming

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
        | CaseState.Active(CaseStatus.Opened, CaseProgress.Paid) -> CaseHeadline.Paid
        | CaseState.Active(CaseStatus.Opened, CaseProgress.PartlyPaid) -> CaseHeadline.PartlyPaid
        | CaseState.Active(CaseStatus.Opened, CaseProgress.Decided) -> CaseHeadline.Decided
        | CaseState.Active(CaseStatus.Opened, CaseProgress.Undecided) -> CaseHeadline.Undecided

/// Recommendations rank only the offered business acts on an active, open case.
module CaseAdvice =
    let recommended state offered =
        let candidates =
            match state with
            | CaseState.Active(CaseStatus.Opened, progress) ->
                let preferred =
                    match progress with
                    | CaseProgress.Undecided -> CaseActKind.Decide
                    | CaseProgress.Decided
                    | CaseProgress.PartlyPaid -> CaseActKind.RecordPayment
                    | CaseProgress.Paid -> CaseActKind.Close

                [ preferred; CaseActKind.AmendRegistration; CaseActKind.Correct ]
            | CaseState.Active(CaseStatus.Closed, _)
            | CaseState.Voided _
            | CaseState.Erased -> []

        candidates |> List.tryFind (fun act -> List.contains act offered)
