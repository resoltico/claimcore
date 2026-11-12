namespace ClaimCore.Domain


open System

/// A retained snapshot that cannot represent accepted state; never an input diagnostic.
[<RequireQualifiedAccess>]
type RecordViolation =
    | InvalidScalar of InputTarget * Violation
    | InvalidFinancialPosition
    | InvalidPaymentContext
    | RevisionOutOfRange
    | ReferenceMismatch

/// Payment facts are loaded with the case under the same transaction lock.
[<RequireQualifiedAccess>]
type CasePaymentContext =
    | NoneRequired
    | Recording of CaseId * Revision * identityReserved: bool
    | Removing of
        CaseId *
        Revision *
        selected: PaymentEntry option *
        remainingDates: PaymentDateBounds option

[<RequireQualifiedAccess>]
type internal AdmittedPaymentContext =
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
    | InvalidContext of RecordViolation

/// Small Result computation expression; ordinary business refusals are values, not exceptions.
module internal ResultFlow =
    type Builder() =
        member _.Bind(value, binder) = Result.bind binder value
        member _.Return(value) = Ok value
        member _.ReturnFrom(value) = value
        member _.Zero() = Ok()

    let result = Builder()
