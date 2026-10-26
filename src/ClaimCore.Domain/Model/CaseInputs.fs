namespace ClaimCore.Domain

open System

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

type PaymentInput = { PaymentDate: string }

[<RequireQualifiedAccess>]
type RegistrationGroup =
    | Keep
    | Replace of RegistrationInput

[<RequireQualifiedAccess>]
type DecisionGroup =
    | Keep
    | Replace of DecisionInput
    | Clear

[<RequireQualifiedAccess>]
type PaymentGroup =
    | Keep
    | Replace of PaymentInput
    | Clear

type CorrectionInput =
    {
        Registration: RegistrationGroup
        Decision: DecisionGroup
        Payment: PaymentGroup
    }

[<RequireQualifiedAccess>]
type UnvalidatedCaseAct =
    | Open of RegistrationInput
    | AmendRegistration of RegistrationInput
    | Correct of CorrectionInput
    | Decide of DecisionInput
    | WithdrawDecision
    | RecordPayment of PaymentInput
    | ClearPayment
    | Close
    | Reopen
    | Void of string
    | Reinstate of string
    | Erase

type UnvalidatedCaseRequest =
    {
        OperationId: Guid
        CaseReference: string
        ExpectedRevision: int64
        Act: UnvalidatedCaseAct
    }

module UnvalidatedCaseActs =
    let kind =
        function
        | UnvalidatedCaseAct.Open _ -> CaseActKind.Business BusinessActKind.Open
        | UnvalidatedCaseAct.AmendRegistration _ ->
            CaseActKind.Business BusinessActKind.AmendRegistration
        | UnvalidatedCaseAct.Correct _ -> CaseActKind.Business BusinessActKind.Correct
        | UnvalidatedCaseAct.Decide _ -> CaseActKind.Business BusinessActKind.Decide
        | UnvalidatedCaseAct.WithdrawDecision ->
            CaseActKind.Business BusinessActKind.WithdrawDecision
        | UnvalidatedCaseAct.RecordPayment _ -> CaseActKind.Business BusinessActKind.RecordPayment
        | UnvalidatedCaseAct.ClearPayment -> CaseActKind.Business BusinessActKind.ClearPayment
        | UnvalidatedCaseAct.Close -> CaseActKind.Business BusinessActKind.Close
        | UnvalidatedCaseAct.Reopen -> CaseActKind.Business BusinessActKind.Reopen
        | UnvalidatedCaseAct.Void _ -> CaseActKind.Stewardship StewardshipActKind.Void
        | UnvalidatedCaseAct.Reinstate _ -> CaseActKind.Stewardship StewardshipActKind.Reinstate
        | UnvalidatedCaseAct.Erase -> CaseActKind.Stewardship StewardshipActKind.Erase

    let name act = kind act |> CaseActKinds.token
