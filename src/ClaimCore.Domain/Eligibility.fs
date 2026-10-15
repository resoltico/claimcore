namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type internal PaymentProgress =
    | Undecided
    | Decided of decision: Validation.Decision
    | Paid of decision: Validation.Decision * paidDate: System.DateOnly

/// Complete groups are retained as typed state; no paid state can omit its decision.
module internal PaymentProgress =
    let decision =
        function
        | PaymentProgress.Undecided -> None
        | PaymentProgress.Decided value
        | PaymentProgress.Paid(value, _) -> Some value

    let paymentDate =
        function
        | PaymentProgress.Paid(_, date) -> Some date
        | _ -> None

    let fromGroups decision payment =
        match decision, payment with
        | None, None -> Ok PaymentProgress.Undecided
        | None, Some _ -> Error CaseRefusal.DecisionRequired
        | Some value, None -> Ok(PaymentProgress.Decided value)
        | Some value, Some date -> Ok(PaymentProgress.Paid(value, date))

module internal Eligibility =
    let private amendment progress =
        match progress with
        | PaymentProgress.Undecided -> Ok()
        | _ -> Error CaseRefusal.AmendmentRequiresUndecided

    let private decision progress =
        match progress with
        | PaymentProgress.Paid _ -> Error CaseRefusal.DecisionAlreadyPaid
        | _ -> Ok()

    let private withdrawal progress =
        match progress with
        | PaymentProgress.Undecided -> Error CaseRefusal.DecisionRequired
        | PaymentProgress.Paid _ -> Error CaseRefusal.DecisionAlreadyPaid
        | PaymentProgress.Decided _ -> Ok()

    let private payment progress =
        match progress with
        | PaymentProgress.Undecided -> Error CaseRefusal.DecisionRequired
        | PaymentProgress.Paid _ -> Error CaseRefusal.PaymentAlreadyRecorded
        | PaymentProgress.Decided value when value.Payable.Value = 0M ->
            Error CaseRefusal.ZeroDecisionCannotBePaid
        | PaymentProgress.Decided _ -> Ok()

    let private clearPayment progress =
        match progress with
        | PaymentProgress.Paid _ -> Ok()
        | _ -> Error CaseRefusal.PaymentNotRecorded

    let private correction progress =
        match progress with
        | PaymentProgress.Undecided -> Error CaseRefusal.CorrectionRequiresExistingValue
        | _ -> Ok()

    let private opened kind progress =
        match kind with
        | CaseActKind.Open -> Error CaseRefusal.AlreadyExists
        | CaseActKind.Reopen -> Error CaseRefusal.AlreadyOpened
        | CaseActKind.Close -> Ok()
        | CaseActKind.AmendRegistration -> amendment progress
        | CaseActKind.CorrectCase -> correction progress
        | CaseActKind.Decide -> decision progress
        | CaseActKind.WithdrawDecision -> withdrawal progress
        | CaseActKind.RecordPayment -> payment progress
        | CaseActKind.ClearPayment -> clearPayment progress
        | _ -> Validation.invalidAct InputTarget.Act ActViolation.StateTransitionMismatch

    let check kind status progress =
        match status, kind with
        | CaseStatus.Closed, CaseActKind.Open -> Error CaseRefusal.AlreadyExists
        | CaseStatus.Closed, CaseActKind.Reopen -> Ok()
        | CaseStatus.Closed, CaseActKind.Close -> Error CaseRefusal.AlreadyClosed
        | CaseStatus.Closed, CaseActKind.CorrectCase -> Ok()
        | CaseStatus.Closed, _ -> Error CaseRefusal.ClosedCase
        | CaseStatus.Opened, _ -> opened kind progress
