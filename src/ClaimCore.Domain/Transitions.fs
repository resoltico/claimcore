namespace ClaimCore.Domain


module internal Transitions =
    let private amendment progress =
        match progress with
        | PaymentProgress.Undecided -> Ok()
        | _ -> Error Refusal.AmendmentRequiresUndecided

    let private decision progress =
        match progress with
        | PaymentProgress.Paid _ -> Error Refusal.DecisionAlreadyPaid
        | _ -> Ok()

    let private withdrawal progress =
        match progress with
        | PaymentProgress.Undecided -> Error Refusal.DecisionRequired
        | PaymentProgress.Paid _ -> Error Refusal.DecisionAlreadyPaid
        | PaymentProgress.Decided _ -> Ok()

    let private payment progress =
        match progress with
        | PaymentProgress.Undecided -> Error Refusal.DecisionRequired
        | PaymentProgress.Paid _ -> Error Refusal.PaymentAlreadyRecorded
        | PaymentProgress.Decided value when value.PayableAmount.Value = 0M ->
            Error Refusal.ZeroDecisionCannotBePaid
        | PaymentProgress.Decided _ -> Ok()

    let private clearPayment progress =
        match progress with
        | PaymentProgress.Paid _ -> Ok()
        | _ -> Error Refusal.PaymentNotRecorded

    let private correction progress =
        match progress with
        | PaymentProgress.Undecided -> Error Refusal.CorrectionRequiresExistingValue
        | _ -> Ok()

    let private opened kind progress =
        match kind with
        | BusinessActKind.Open -> Error Refusal.CaseAlreadyExists
        | BusinessActKind.Reopen -> Error Refusal.AlreadyOpened
        | BusinessActKind.Close -> Ok()
        | BusinessActKind.AmendRegistration -> amendment progress
        | BusinessActKind.Correct -> correction progress
        | BusinessActKind.Decide -> decision progress
        | BusinessActKind.WithdrawDecision -> withdrawal progress
        | BusinessActKind.RecordPayment -> payment progress
        | BusinessActKind.ClearPayment -> clearPayment progress

    let private business kind status progress =
        match status, kind with
        | CaseStatus.Closed, BusinessActKind.Open -> Error Refusal.CaseAlreadyExists
        | CaseStatus.Closed, BusinessActKind.Reopen -> Ok()
        | CaseStatus.Closed, BusinessActKind.Close -> Error Refusal.AlreadyClosed
        | CaseStatus.Closed, BusinessActKind.Correct -> Ok()
        | CaseStatus.Closed, _ -> Error Refusal.CaseClosed
        | CaseStatus.Opened, _ -> opened kind progress

    /// Existence and revision precedence belong to Case.decide. This total rule
    /// also serves availability, including the erased and already-existing states.
    let check (state: CaseState<Claim>) kind =
        match state, kind with
        | CaseState.Erased, _ -> Error Refusal.CaseErased
        | (CaseState.Active _ | CaseState.Voided _), CaseActKind.Business BusinessActKind.Open ->
            Error Refusal.CaseAlreadyExists
        | CaseState.Voided _, CaseActKind.Stewardship StewardshipActKind.Void ->
            Error Refusal.AlreadyVoided
        | CaseState.Voided _, CaseActKind.Business _ -> Error Refusal.CaseVoided
        | CaseState.Active _, CaseActKind.Stewardship StewardshipActKind.Reinstate ->
            Error Refusal.NotVoided
        | (CaseState.Active _ | CaseState.Voided _),
          CaseActKind.Stewardship(StewardshipActKind.Void | StewardshipActKind.Reinstate | StewardshipActKind.Erase) ->
            Ok()
        | CaseState.Active claim, CaseActKind.Business kind ->
            business kind claim.Status claim.Progress
