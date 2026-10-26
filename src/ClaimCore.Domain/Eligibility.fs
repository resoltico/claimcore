namespace ClaimCore.Domain

open ClaimCore.Contract

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
        | None, Some _ -> Error Refusal.DecisionRequired
        | Some value, None -> Ok(PaymentProgress.Decided value)
        | Some value, Some date -> Ok(PaymentProgress.Paid(value, date))

module internal Eligibility =
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

    let check kind status progress =
        match status, kind with
        | CaseStatus.Closed, BusinessActKind.Open -> Error Refusal.CaseAlreadyExists
        | CaseStatus.Closed, BusinessActKind.Reopen -> Ok()
        | CaseStatus.Closed, BusinessActKind.Close -> Error Refusal.AlreadyClosed
        | CaseStatus.Closed, BusinessActKind.Correct -> Ok()
        | CaseStatus.Closed, _ -> Error Refusal.CaseClosed
        | CaseStatus.Opened, _ -> opened kind progress
