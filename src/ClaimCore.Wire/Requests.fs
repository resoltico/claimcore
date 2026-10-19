namespace ClaimCore.Wire

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

type Principal =
    {
        Kind: string
        Issuer: string
        Subject: string
    }

[<RequireQualifiedAccess>]
type Scope =
    | Installation
    | Case of string

type GrantChange =
    {
        Role: string
        Scope: Scope
        Active: bool
    }

[<RequireQualifiedAccess>]
type AuthorityAct =
    | RegisterActor
    | SetGrant of GrantChange
    | SetEnabled of bool

type AuthorityRequest =
    {
        OperationId: Guid
        Target: Principal
        Act: AuthorityAct
    }

type PageRequest = { Cursor: string option; Limit: int64 }

type CaseGetRequest = { CaseReference: string }

type HistoryRequest =
    {
        CaseReference: string
        Detail: string
        Cursor: string option
        Limit: int64
    }

type GrantsRequest =
    {
        ActorId: Guid
        Cursor: string option
        Limit: int64
    }

type ObserveRequest = { OperationId: Guid }

[<RequireQualifiedAccess>]
type WireRequest =
    | Session
    | SessionLogout
    | Context
    | CaseGet of CaseGetRequest
    | CaseList of PageRequest
    | CaseHistory of HistoryRequest
    | CasePreview of CaseRequest
    | CaseExecute of CaseRequest
    | OperationObserve of ObserveRequest
    | AuthorityList of PageRequest
    | AuthorityGrants of GrantsRequest
    | AuthorityExecute of AuthorityRequest

module WireRequest =
    let endpoint =
        function
        | WireRequest.Session -> Endpoint.Session
        | WireRequest.SessionLogout -> Endpoint.SessionLogout
        | WireRequest.Context -> Endpoint.Context
        | WireRequest.CaseGet _ -> Endpoint.CaseGet
        | WireRequest.CaseList _ -> Endpoint.CaseList
        | WireRequest.CaseHistory _ -> Endpoint.CaseHistory
        | WireRequest.CasePreview _ -> Endpoint.CasePreview
        | WireRequest.CaseExecute _ -> Endpoint.CaseExecute
        | WireRequest.OperationObserve _ -> Endpoint.OperationObserve
        | WireRequest.AuthorityList _ -> Endpoint.AuthorityList
        | WireRequest.AuthorityGrants _ -> Endpoint.AuthorityGrants
        | WireRequest.AuthorityExecute _ -> Endpoint.AuthorityExecute

    let operationId =
        function
        | WireRequest.CasePreview value
        | WireRequest.CaseExecute value -> Some value.OperationId
        | WireRequest.AuthorityExecute value -> Some value.OperationId
        | WireRequest.OperationObserve value -> Some value.OperationId
        | _ -> None
