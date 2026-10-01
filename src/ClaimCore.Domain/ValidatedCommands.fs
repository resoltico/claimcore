namespace ClaimCore.Domain

open System

/// Parsed command inputs. The original public request retains authored text for replay identity.
[<RequireQualifiedAccess>]
type internal ValidatedRegistrationCorrection =
    | Keep
    | Replace of Validation.Facts

[<RequireQualifiedAccess>]
type internal ValidatedDecisionCorrection =
    | Keep
    | Replace of Validation.Decision
    | Clear

[<RequireQualifiedAccess>]
type internal ValidatedPaymentCorrection =
    | Keep
    | Replace of DateOnly
    | Clear

type internal ValidatedCaseCorrection =
    {
        Registration: ValidatedRegistrationCorrection
        Decision: ValidatedDecisionCorrection
        Payment: ValidatedPaymentCorrection
    }

[<RequireQualifiedAccess>]
type internal ValidatedCommand =
    | Open of Validation.Facts
    | AmendRegistration of Validation.Facts
    | CorrectCase of ValidatedCaseCorrection
    | Decide of Validation.Decision
    | WithdrawDecision
    | RecordPayment of DateOnly
    | ClearPayment
    | Close
    | Reopen
