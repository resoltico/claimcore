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
type Command =
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
type CommandKind =
    | Open
    | AmendRegistration
    | CorrectCase
    | Decide
    | WithdrawDecision
    | RecordPayment
    | ClearPayment
    | Close
    | Reopen

/// OperationId and ExpectedVersion are technical replay/concurrency controls, not claims entries.
type CommandRequest =
    {
        OperationId: Guid
        CaseReference: string
        ExpectedVersion: int64
        Command: Command
    }

/// Separate technical revision from the renderable business record.
type CaseView = { Fields: CaseFields; Version: int64 }

[<RequireQualifiedAccess>]
type DomainError =
    | InvalidInput of field: string * message: string
    | NotFound
    | AlreadyExists
    | VersionConflict of actualVersion: int64
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

/// Small Result computation expression; ordinary business refusals are values, not exceptions.
module internal ResultFlow =
    type Builder() =
        member _.Bind(value, binder) = Result.bind binder value
        member _.Return(value) = Ok value
        member _.ReturnFrom(value) = value
        member _.Zero() = Ok()

    let result = Builder()
