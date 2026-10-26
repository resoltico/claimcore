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
