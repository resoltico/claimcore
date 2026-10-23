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
    | CorrectCase of Correction
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
