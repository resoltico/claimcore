namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type internal PaymentProgress =
    | Undecided
    | Decided of decision: Validation.Decision
    | Paid of decision: Validation.Decision * paidDate: System.DateOnly

module internal Eligibility =
    let private amendment progress =
        match progress with
        | PaymentProgress.Undecided -> Ok()
        | _ -> Error DomainError.AmendmentRequiresUndecided

    let private decision progress =
        match progress with
        | PaymentProgress.Paid _ -> Error DomainError.DecisionAlreadyPaid
        | _ -> Ok()

    let private withdrawal progress =
        match progress with
        | PaymentProgress.Undecided -> Error DomainError.DecisionRequired
        | PaymentProgress.Paid _ -> Error DomainError.DecisionAlreadyPaid
        | PaymentProgress.Decided _ -> Ok()

    let private payment progress =
        match progress with
        | PaymentProgress.Undecided -> Error DomainError.DecisionRequired
        | PaymentProgress.Paid _ -> Error DomainError.PaymentAlreadyRecorded
        | PaymentProgress.Decided value when value.Payable.Value = 0M ->
            Error DomainError.ZeroDecisionCannotBePaid
        | PaymentProgress.Decided _ -> Ok()

    let private clearPayment progress =
        match progress with
        | PaymentProgress.Paid _ -> Ok()
        | _ -> Error DomainError.PaymentNotRecorded

    let private opened kind progress =
        match kind with
        | CommandKind.Open -> Error DomainError.AlreadyExists
        | CommandKind.Reopen -> Error DomainError.AlreadyOpened
        | CommandKind.Close -> Ok()
        | CommandKind.AmendRegistration -> amendment progress
        | CommandKind.Decide -> decision progress
        | CommandKind.WithdrawDecision -> withdrawal progress
        | CommandKind.RecordPayment -> payment progress
        | CommandKind.ClearPayment -> clearPayment progress

    let check kind status progress =
        match status, kind with
        | CaseStatus.Closed, CommandKind.Open -> Error DomainError.AlreadyExists
        | CaseStatus.Closed, CommandKind.Reopen -> Ok()
        | CaseStatus.Closed, CommandKind.Close -> Error DomainError.AlreadyClosed
        | CaseStatus.Closed, _ -> Error DomainError.ClosedCase
        | CaseStatus.Opened, _ -> opened kind progress
