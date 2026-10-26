namespace ClaimCore.Domain

open System

/// Parsed act inputs. The original public request retains authored text for replay identity.
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
type internal ValidatedAct =
    | Open of Validation.Facts
    | AmendRegistration of Validation.Facts
    | Correct of ValidatedCaseCorrection
    | Decide of Validation.Decision
    | WithdrawDecision
    | RecordPayment of DateOnly
    | ClearPayment
    | Close
    | Reopen

module internal ValidatedActs =
    let kind =
        function
        | ValidatedAct.Open _ -> BusinessActKind.Open
        | ValidatedAct.AmendRegistration _ -> BusinessActKind.AmendRegistration
        | ValidatedAct.Correct _ -> BusinessActKind.Correct
        | ValidatedAct.Decide _ -> BusinessActKind.Decide
        | ValidatedAct.WithdrawDecision -> BusinessActKind.WithdrawDecision
        | ValidatedAct.RecordPayment _ -> BusinessActKind.RecordPayment
        | ValidatedAct.ClearPayment -> BusinessActKind.ClearPayment
        | ValidatedAct.Close -> BusinessActKind.Close
        | ValidatedAct.Reopen -> BusinessActKind.Reopen
