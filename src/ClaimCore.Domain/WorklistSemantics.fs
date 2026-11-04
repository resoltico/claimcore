namespace ClaimCore.Domain

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

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseView =
    let vocabulary =
        Vocabulary.ofUnion<CaseView> "caseView" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ReminderView =
    let vocabulary =
        Vocabulary.ofUnion<ReminderView> "reminderView" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

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

/// A case has one headline, independent of the renderer and of the fields it displays.
[<RequireQualifiedAccess>]
type CaseHeadline =
    | Erased
    | Voided
    | Closed
    | Paid
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
                    | CaseProgress.Decided -> CaseActKind.RecordPayment
                    | CaseProgress.Paid -> CaseActKind.Close

                [ preferred; CaseActKind.AmendRegistration; CaseActKind.Correct ]
            | CaseState.Active(CaseStatus.Closed, _)
            | CaseState.Voided _
            | CaseState.Erased -> []

        candidates |> List.tryFind (fun act -> List.contains act offered)
