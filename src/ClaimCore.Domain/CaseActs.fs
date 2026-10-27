namespace ClaimCore.Domain

open System

/// Parsed act inputs. The original public request retains authored text for replay identity.
[<RequireQualifiedAccess>]
type RegistrationChange =
    | Keep
    | Replace of Registration

[<RequireQualifiedAccess>]
type DecisionChange =
    | Keep
    | Replace of Decision
    | Clear

[<RequireQualifiedAccess>]
type PaymentChange =
    | Keep
    | Replace of CalendarDate
    | Clear

type CaseCorrection =
    {
        Registration: RegistrationChange
        Decision: DecisionChange
        Payment: PaymentChange
    }

[<RequireQualifiedAccess>]
type CaseAct =
    | Open of Registration
    | AmendRegistration of Registration
    | Correct of CaseCorrection
    | Decide of Decision
    | WithdrawDecision
    | RecordPayment of CalendarDate
    | ClearPayment
    | Close
    | Reopen
    | Void of Reason
    | Reinstate of Reason
    | Erase

module CaseActs =
    let kind =
        function
        | CaseAct.Open _ -> CaseActKind.Open
        | CaseAct.AmendRegistration _ -> CaseActKind.AmendRegistration
        | CaseAct.Correct _ -> CaseActKind.Correct
        | CaseAct.Decide _ -> CaseActKind.Decide
        | CaseAct.WithdrawDecision -> CaseActKind.WithdrawDecision
        | CaseAct.RecordPayment _ -> CaseActKind.RecordPayment
        | CaseAct.ClearPayment -> CaseActKind.ClearPayment
        | CaseAct.Close -> CaseActKind.Close
        | CaseAct.Reopen -> CaseActKind.Reopen
        | CaseAct.Void _ -> CaseActKind.Void
        | CaseAct.Reinstate _ -> CaseActKind.Reinstate
        | CaseAct.Erase -> CaseActKind.Erase

    let name act = kind act |> CaseActKind.token
