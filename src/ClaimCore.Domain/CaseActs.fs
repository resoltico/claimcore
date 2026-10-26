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
type BusinessAct =
    | Open of Registration
    | AmendRegistration of Registration
    | Correct of CaseCorrection
    | Decide of Decision
    | WithdrawDecision
    | RecordPayment of CalendarDate
    | ClearPayment
    | Close
    | Reopen

module BusinessActs =
    let kind =
        function
        | BusinessAct.Open _ -> BusinessActKind.Open
        | BusinessAct.AmendRegistration _ -> BusinessActKind.AmendRegistration
        | BusinessAct.Correct _ -> BusinessActKind.Correct
        | BusinessAct.Decide _ -> BusinessActKind.Decide
        | BusinessAct.WithdrawDecision -> BusinessActKind.WithdrawDecision
        | BusinessAct.RecordPayment _ -> BusinessActKind.RecordPayment
        | BusinessAct.ClearPayment -> BusinessActKind.ClearPayment
        | BusinessAct.Close -> BusinessActKind.Close
        | BusinessAct.Reopen -> BusinessActKind.Reopen

/// Business work and stewardship carry different parsed payloads.
[<RequireQualifiedAccess>]
type CaseAct =
    | Business of BusinessAct
    | Void of Reason
    | Reinstate of Reason
    | Erase

module CaseActs =
    let kind =
        function
        | CaseAct.Business act -> CaseActKind.Business(BusinessActs.kind act)
        | CaseAct.Void _ -> CaseActKind.Stewardship StewardshipActKind.Void
        | CaseAct.Reinstate _ -> CaseActKind.Stewardship StewardshipActKind.Reinstate
        | CaseAct.Erase -> CaseActKind.Stewardship StewardshipActKind.Erase

    let name act = kind act |> CaseActKinds.token
