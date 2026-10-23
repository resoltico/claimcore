namespace ClaimCore.Domain

open ClaimCore.Contract

open System

/// Exactly the thirteen business fields. Technical revision, receipt and UI metadata are separate.
/// Optional fields remain absent until the operator records a decision or payment.
type CaseFields =
    {
        IncidentDate: string
        IncidentNotificationDate: string
        IncidentCountry: string
        ClaimantName: string
        InsurerName: string
        ClaimedAmount: string
        ClaimedCurrency: string
        CaseReference: string
        PaymentDecisionDate: string option
        PayableAmount: string option
        PayableCurrency: string option
        PaymentDate: string option
        Status: CaseStatus
    }

[<RequireQualifiedAccess>]
type CaseView =
    | Live of fields: CaseFields * disposition: Disposition
    | Erased

type CaseSnapshot =
    {
        Reference: string
        Revision: int64
        View: CaseView
    }

/// Exact integer range shared by Domain revisions and interoperable JSON records.
module SafeInteger =
    [<Literal>]
    let Maximum = 9007199254740991L

module Revision =
    [<Literal>]
    let Maximum = SafeInteger.Maximum

[<RequireQualifiedAccess>]
type CaseRefusal =
    | InvalidInput of field: InputTarget * violation: InputViolation
    | NotFound
    | AlreadyExists
    | RevisionConflict of actualRevision: int64
    | RevisionExhausted
    | CaseErased
    | CaseVoided
    | AlreadyVoided
    | NotVoided
    | ClosedCase
    | AmendmentRequiresUndecided
    | CorrectionNoChanges
    | CorrectionRequiresExistingValue
    | DecisionRequired
    | PaymentAlreadyRecorded
    | PaymentNotRecorded
    | DecisionAlreadyPaid
    | AlreadyClosed
    | AlreadyOpened
    | ZeroDecisionCannotBePaid

/// A retained snapshot that cannot represent accepted state; never an input diagnostic.
[<RequireQualifiedAccess>]
type RecordViolation =
    | InvalidScalar of InputTarget * InputViolation
    | IncompleteDecision
    | InvalidProgress
    | NonCanonicalFields
    | RevisionOutOfRange
    | ReferenceMismatch

/// Small Result computation expression; ordinary business refusals are values, not exceptions.
module internal ResultFlow =
    type Builder() =
        member _.Bind(value, binder) = Result.bind binder value
        member _.Return(value) = Ok value
        member _.ReturnFrom(value) = value
        member _.Zero() = Ok()

    let result = Builder()
