namespace ClaimCore.Contract

open System

type Registration =
    {
        IncidentDate: string
        IncidentNotificationDate: string
        IncidentCountry: string
        ClaimantName: string
        InsurerName: string
        ClaimedAmount: string
        ClaimedCurrency: string
    }

type Decision =
    {
        PaymentDecisionDate: string
        PayableAmount: string
        PayableCurrency: string
    }

type Payment = { PaymentDate: string }

[<RequireQualifiedAccess>]
type RegistrationGroup =
    | Keep
    | Replace of Registration

[<RequireQualifiedAccess>]
type DecisionGroup =
    | Keep
    | Replace of Decision
    | Clear

[<RequireQualifiedAccess>]
type PaymentGroup =
    | Keep
    | Replace of Payment
    | Clear

type Correction =
    {
        Registration: RegistrationGroup
        Decision: DecisionGroup
        Payment: PaymentGroup
    }

[<RequireQualifiedAccess>]
type CaseAct =
    | Open of Registration
    | AmendRegistration of Registration
    | Correct of Correction
    | Decide of Decision
    | WithdrawDecision
    | RecordPayment of Payment
    | ClearPayment
    | Close
    | Reopen
    | Void of string
    | Reinstate of string
    | Erase

type CaseRequest =
    {
        OperationId: Guid
        CaseReference: string
        ExpectedRevision: int64
        Act: CaseAct
    }

module CaseActs =
    let kind =
        function
        | CaseAct.Open _ -> CaseActKind.Business BusinessActKind.Open
        | CaseAct.AmendRegistration _ -> CaseActKind.Business BusinessActKind.AmendRegistration
        | CaseAct.Correct _ -> CaseActKind.Business BusinessActKind.Correct
        | CaseAct.Decide _ -> CaseActKind.Business BusinessActKind.Decide
        | CaseAct.WithdrawDecision -> CaseActKind.Business BusinessActKind.WithdrawDecision
        | CaseAct.RecordPayment _ -> CaseActKind.Business BusinessActKind.RecordPayment
        | CaseAct.ClearPayment -> CaseActKind.Business BusinessActKind.ClearPayment
        | CaseAct.Close -> CaseActKind.Business BusinessActKind.Close
        | CaseAct.Reopen -> CaseActKind.Business BusinessActKind.Reopen
        | CaseAct.Void _ -> CaseActKind.Stewardship StewardshipActKind.Void
        | CaseAct.Reinstate _ -> CaseActKind.Stewardship StewardshipActKind.Reinstate
        | CaseAct.Erase -> CaseActKind.Stewardship StewardshipActKind.Erase

    let name act = kind act |> CaseActKinds.token
