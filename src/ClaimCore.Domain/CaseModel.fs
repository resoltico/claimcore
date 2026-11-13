namespace ClaimCore.Domain


open System

/// A retained snapshot that cannot represent accepted state; never an input diagnostic.
[<RequireQualifiedAccess>]
type RecordViolation =
    | InvalidScalar of InputTarget * Violation
    | InvalidFinancialPosition
    | InvalidPaymentState
    | RevisionOutOfRange
    | ReferenceMismatch

[<RequireQualifiedAccess>]
type internal PaymentTransition =
    | NoneRequired
    | Recording of identityReserved: bool
    | Removing of (PaymentEntry * FinancialPosition) option

type CaseDecision =
    {
        After: CaseSnapshot
        Payment: PaymentDifference option
    }

[<RequireQualifiedAccess>]
type CaseDecisionError =
    | Refused of Refusal
    | InvalidState of RecordViolation

/// Small Result computation expression; ordinary business refusals are values, not exceptions.
module internal ResultFlow =
    type Builder() =
        member _.Bind(value, binder) = Result.bind binder value
        member _.Return(value) = Ok value
        member _.ReturnFrom(value) = value
        member _.Zero() = Ok()

    let result = Builder()
