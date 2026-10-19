namespace ClaimCore.Domain

open System

/// The two case statuses requested by the operator. Payment never changes this implicitly.
[<RequireQualifiedAccess>]
type CaseStatus =
    | Opened
    | Closed

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

/// Registration is only the initial subset of CaseFields; case reference is the request target.
/// IncidentNotificationDate is THIS handler's notification date, regardless of who notified them.
/// InsurerName is an allegation, not an adjudication of ultimate liability.
type RegistrationInput =
    {
        IncidentDate: string
        IncidentNotificationDate: string
        IncidentCountry: string
        ClaimantName: string
        InsurerName: string
        ClaimedAmount: string
        ClaimedCurrency: string
    }

type DecisionInput =
    {
        PaymentDecisionDate: string
        PayableAmount: string
        PayableCurrency: string
    }

/// A correction reads the retained group from authoritative case state instead of trusting a
/// duplicated client value. Replacement preserves authored scalar text for operation identity.
[<RequireQualifiedAccess>]
type RegistrationCorrection =
    | Keep
    | Replace of RegistrationInput

[<RequireQualifiedAccess>]
type DecisionCorrection =
    | Keep
    | Replace of DecisionInput
    | Clear

[<RequireQualifiedAccess>]
type PaymentCorrection =
    | Keep
    | Replace of paymentDate: string
    | Clear

/// One atomic factual correction. It has no case-reference or status field and therefore cannot
/// expand the thirteen-field business record.
type CaseCorrection =
    {
        Registration: RegistrationCorrection
        Decision: DecisionCorrection
        Payment: PaymentCorrection
    }

/// Commands only maintain the requested fields. No correction-note or other business field is added.
[<RequireQualifiedAccess>]
type BusinessCommand =
    | Open of registration: RegistrationInput
    | AmendRegistration of registration: RegistrationInput
    | CorrectCase of correction: CaseCorrection
    | Decide of decision: DecisionInput
    | WithdrawDecision
    | RecordPayment of paymentDate: string
    | ClearPayment
    | Close
    | Reopen

/// Closed operation vocabulary; names and payload kinds belong to the core, not a renderer.
[<RequireQualifiedAccess>]
type BusinessActKind =
    | Open
    | AmendRegistration
    | CorrectCase
    | Decide
    | WithdrawDecision
    | RecordPayment
    | ClearPayment
    | Close
    | Reopen

[<RequireQualifiedAccess>]
type StewardshipActKind =
    | Void
    | Reinstate
    | Erase

[<RequireQualifiedAccess>]
type CaseActKind =
    | Business of BusinessActKind
    | Stewardship of StewardshipActKind

/// Stewardship changes the case record without asserting business facts.
[<RequireQualifiedAccess>]
type Disposition =
    | Active
    | Voided

[<RequireQualifiedAccess>]
type StewardshipAct =
    | Void of reason: string
    | Reinstate of reason: string
    | Erase

[<RequireQualifiedAccess>]
type CaseAct =
    | Business of BusinessCommand
    | Stewardship of StewardshipAct

/// Caller-authored content remains exact even when its target compares case-insensitively.
type CaseRequest =
    {
        OperationId: Guid
        CaseReference: string
        ExpectedRevision: int64
        Act: CaseAct
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
