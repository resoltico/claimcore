namespace ClaimCore.Domain

open System
open ClaimCore.Json

[<RequireQualifiedAccess>]
type RecordGroup =
    | Registration
    | Decision
    | Payment

[<RequireQualifiedAccess>]
type CorrectionGroupAction =
    | Keep
    | Replace
    | Clear

/// Closed operation vocabulary; names and payload kinds belong to the core, not a renderer.
[<RequireQualifiedAccess>]
type BusinessActKind =
    | Open
    | AmendRegistration
    | Correct
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

[<RequireQualifiedAccess>]
type BusinessField =
    | IncidentDate
    | IncidentNotificationDate
    | IncidentCountry
    | ClaimantName
    | InsurerName
    | ClaimedAmount
    | ClaimedCurrency
    | CaseReference
    | PaymentDecisionDate
    | PayableAmount
    | PayableCurrency
    | PaymentDate
    | Status

[<RequireQualifiedAccess>]
type InputTarget =
    | Field of BusinessField
    | OperationId
    | ExpectedRevision
    | Group of RecordGroup
    | Reason
    | PrincipalIssuer
    | PrincipalSubject
    | Limit
    | Cursor

[<RequireQualifiedAccess>]
type AuthorityKind =
    | ProvisionOwner
    | Register
    | Grant
    | Revoke
    | Enable
    | Disable

/// The complete act vocabulary combines the disjoint case and authority subjects.
[<RequireQualifiedAccess>]
type ActKind =
    | Case of CaseActKind
    | Authority of AuthorityKind

[<RequireQualifiedAccess>]
type ActorEndpoint =
    | Preview
    | Execute
    | CaseGet
    | CaseList
    | CaseHistory
    | Observe
    | Resolve
    | AuthorityList
    | AuthorityGrants
    | Context

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

/// Unvalidated subject facts; Application admits the canonical principal key.
type Principal =
    {
        Kind: string
        Issuer: string
        Subject: string
    }

/// Caller-authored scope; accepted grants resolve case references to case IDs.
[<RequireQualifiedAccess>]
type GrantTarget =
    | Installation
    | CaseReference of string

type GrantChange = { Role: string; Scope: GrantTarget }

[<RequireQualifiedAccess>]
type AuthorityAct =
    | Register
    | Grant of GrantChange
    | Revoke of GrantChange
    | Enable
    | Disable

type AuthorityRequest =
    {
        OperationId: Guid
        Target: Principal
        Act: AuthorityAct
    }

module AuthorityActs =
    let kind =
        function
        | AuthorityAct.Register -> AuthorityKind.Register
        | AuthorityAct.Grant _ -> AuthorityKind.Grant
        | AuthorityAct.Revoke _ -> AuthorityKind.Revoke
        | AuthorityAct.Enable -> AuthorityKind.Enable
        | AuthorityAct.Disable -> AuthorityKind.Disable

module RecordGroup =
    let vocabulary =
        Vocabulary.define
            "group"
            [ RecordGroup.Registration; RecordGroup.Decision; RecordGroup.Payment ]
            (function
             | RecordGroup.Registration -> "registration"
             | RecordGroup.Decision -> "decision"
             | RecordGroup.Payment -> "payment")

    let token = Vocabulary.token vocabulary

module CorrectionActions =
    let vocabulary =
        Vocabulary.define
            "token"
            [
                CorrectionGroupAction.Keep
                CorrectionGroupAction.Replace
                CorrectionGroupAction.Clear
            ]
            (function
             | CorrectionGroupAction.Keep -> "KEEP"
             | CorrectionGroupAction.Replace -> "REPLACE"
             | CorrectionGroupAction.Clear -> "CLEAR")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    let forGroup =
        function
        | RecordGroup.Registration -> [ CorrectionGroupAction.Keep; CorrectionGroupAction.Replace ]
        | RecordGroup.Decision
        | RecordGroup.Payment -> all

module BusinessActKinds =
    let vocabulary =
        Vocabulary.define
            "act"
            [
                BusinessActKind.Open
                BusinessActKind.AmendRegistration
                BusinessActKind.Correct
                BusinessActKind.Decide
                BusinessActKind.WithdrawDecision
                BusinessActKind.RecordPayment
                BusinessActKind.ClearPayment
                BusinessActKind.Close
                BusinessActKind.Reopen
            ]
            (function
             | BusinessActKind.Open -> "OPEN"
             | BusinessActKind.AmendRegistration -> "AMEND_REGISTRATION"
             | BusinessActKind.Correct -> "CORRECT"
             | BusinessActKind.Decide -> "DECIDE"
             | BusinessActKind.WithdrawDecision -> "WITHDRAW_DECISION"
             | BusinessActKind.RecordPayment -> "RECORD_PAYMENT"
             | BusinessActKind.ClearPayment -> "CLEAR_PAYMENT"
             | BusinessActKind.Close -> "CLOSE"
             | BusinessActKind.Reopen -> "REOPEN")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

module StewardshipActKinds =
    let vocabulary =
        Vocabulary.define
            "act"
            [
                StewardshipActKind.Void
                StewardshipActKind.Reinstate
                StewardshipActKind.Erase
            ]
            (function
             | StewardshipActKind.Void -> "VOID"
             | StewardshipActKind.Reinstate -> "REINSTATE"
             | StewardshipActKind.Erase -> "ERASE")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

module CaseStatuses =
    let vocabulary =
        Vocabulary.define "token" [ CaseStatus.Opened; CaseStatus.Closed ] (function
            | CaseStatus.Opened -> "OPENED"
            | CaseStatus.Closed -> "CLOSED")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

module CaseActKinds =
    let vocabulary =
        Vocabulary.define
            "act"
            ((BusinessActKinds.all |> List.map CaseActKind.Business)
             @ (StewardshipActKinds.all |> List.map CaseActKind.Stewardship))
            (function
             | CaseActKind.Business value -> BusinessActKinds.token value
             | CaseActKind.Stewardship value -> StewardshipActKinds.token value)

    let business = BusinessActKinds.all |> List.map CaseActKind.Business
    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

module AuthorityKinds =
    let vocabulary =
        Vocabulary.define
            "act"
            [
                AuthorityKind.ProvisionOwner
                AuthorityKind.Register
                AuthorityKind.Grant
                AuthorityKind.Revoke
                AuthorityKind.Enable
                AuthorityKind.Disable
            ]
            (function
             | AuthorityKind.ProvisionOwner -> "PROVISION_OWNER"
             | AuthorityKind.Register -> "REGISTER"
             | AuthorityKind.Grant -> "GRANT"
             | AuthorityKind.Revoke -> "REVOKE"
             | AuthorityKind.Enable -> "ENABLE"
             | AuthorityKind.Disable -> "DISABLE")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

module ActKinds =
    let vocabulary =
        Vocabulary.define
            "act"
            ((CaseActKinds.all |> List.map ActKind.Case)
             @ (AuthorityKinds.all |> List.map ActKind.Authority))
            (function
             | ActKind.Case value -> CaseActKinds.token value
             | ActKind.Authority value -> AuthorityKinds.token value)

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

module ActorEndpoints =
    let vocabulary =
        Vocabulary.define
            "endpoint"
            [
                ActorEndpoint.Preview
                ActorEndpoint.Execute
                ActorEndpoint.CaseGet
                ActorEndpoint.CaseList
                ActorEndpoint.CaseHistory
                ActorEndpoint.Observe
                ActorEndpoint.Resolve
                ActorEndpoint.AuthorityList
                ActorEndpoint.AuthorityGrants
                ActorEndpoint.Context
            ]
            (function
             | ActorEndpoint.Preview -> "operation.preview"
             | ActorEndpoint.Execute -> "operation.execute"
             | ActorEndpoint.CaseGet -> "case.get"
             | ActorEndpoint.CaseList -> "case.list"
             | ActorEndpoint.CaseHistory -> "case.history"
             | ActorEndpoint.Observe -> "operation.observe"
             | ActorEndpoint.Resolve -> "operation.resolve"
             | ActorEndpoint.AuthorityList -> "authority.list"
             | ActorEndpoint.AuthorityGrants -> "authority.grants"
             | ActorEndpoint.Context -> "context")

    let all = Vocabulary.all vocabulary

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

/// Accepted registration values retain their validated structure.
type Registration =
    {
        IncidentDate: CalendarDate
        IncidentNotificationDate: CalendarDate
        IncidentCountry: CountryCode
        ClaimantName: PartyName
        InsurerName: PartyName
        ClaimedAmount: Money
    }

type Decision =
    {
        PaymentDecisionDate: CalendarDate
        PayableAmount: Money
    }

type Payment = { PaymentDate: CalendarDate }

/// Accepted facts share the authored group types; a decision is complete or absent.
type CaseFields =
    {
        CaseReference: CaseReference
        Status: CaseStatus
        Registration: Registration
        Decision: Decision option
        Payment: Payment option
    }

/// Lifecycle is independent of the payload carried by a full view or summary.
[<RequireQualifiedAccess>]
type CaseState<'fields> =
    | Active of 'fields
    | Voided of 'fields
    | Erased

module CaseState =
    let map mapping =
        function
        | CaseState.Active fields -> CaseState.Active(mapping fields)
        | CaseState.Voided fields -> CaseState.Voided(mapping fields)
        | CaseState.Erased -> CaseState.Erased

    let fields =
        function
        | CaseState.Active fields
        | CaseState.Voided fields -> Some fields
        | CaseState.Erased -> None

    let vocabulary =
        Vocabulary.define
            "token"
            [ CaseState.Active(); CaseState.Voided(); CaseState.Erased ]
            (function
             | CaseState.Active() -> "ACTIVE"
             | CaseState.Voided() -> "VOIDED"
             | CaseState.Erased -> "ERASED")

    let all = Vocabulary.all vocabulary

    let token state =
        Vocabulary.token vocabulary (map ignore state)

/// A disclosure cannot carry an erased state.
[<RequireQualifiedAccess>]
type LiveCase =
    | Active of CaseFields
    | Voided of CaseFields

module LiveCase =
    let toState =
        function
        | LiveCase.Active fields -> CaseState.Active fields
        | LiveCase.Voided fields -> CaseState.Voided fields

    let ofState =
        function
        | CaseState.Active fields -> Some(LiveCase.Active fields)
        | CaseState.Voided fields -> Some(LiveCase.Voided fields)
        | CaseState.Erased -> None

type CaseSnapshot =
    {
        Reference: CaseReference
        Revision: int64
        State: CaseState<CaseFields>
    }

[<RequireQualifiedAccess>]
type FieldValueKind =
    | CalendarDate
    | Country
    | PartyName
    | Amount
    | Currency
    | Reference
    | Status

type FieldDeclaration =
    {
        Field: BusinessField
        Key: string
        Kind: FieldValueKind
        Group: RecordGroup option
        AllowsAbsence: bool
        RetainedOnErasure: bool
    }

[<NoEquality; NoComparison>]
type GroupDeclaration<'Record> =
    private
        {
            Members: BusinessField list
            Build: Codec<string> -> Codec<'Record>
        }

    member this.Fields = this.Members
    member this.Codec text = this.Build text

[<NoEquality; NoComparison>]
type private GroupFields<'Record, 'Ctor> =
    {
        Members: BusinessField list
        Build: Codec<string> -> Fields<'Record, 'Ctor>
    }

[<NoEquality; NoComparison>]
type CaseValues<'Values> =
    {
        Create: 'Values -> UnvalidatedCaseAct
        Extract: UnvalidatedCaseAct -> 'Values option
        Prefill: bool
    }

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type CasePayload =
    | NoPayload of UnvalidatedCaseAct
    | Registration of CaseValues<RegistrationInput>
    | Decision of CaseValues<DecisionInput>
    | Payment of CaseValues<PaymentInput>
    | Reason of CaseValues<string>
    | Correction of CaseValues<CorrectionInput>

[<RequireQualifiedAccess>]
type RevisionInput =
    | New
    | Expected

[<NoEquality; NoComparison>]
type CaseActDeclaration =
    {
        Kind: CaseActKind
        RevisionMode: RevisionInput
        Payload: CasePayload
    }

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type AuthorityPayload =
    | Target of AuthorityAct
    | Grant of create: (GrantChange -> AuthorityAct) * extract: (AuthorityAct -> GrantChange option)

[<NoEquality; NoComparison>]
type AuthorityActDeclaration =
    {
        Kind: AuthorityKind
        Payload: AuthorityPayload
    }

[<RequireQualifiedAccess>]
type HistoryDetail =
    | Summary
    | Full

type PageRequest =
    {
        AfterCursor: string option
        Limit: int
    }

type HistoryRequest =
    {
        CaseReference: string
        AfterCursor: string option
        Limit: int
        Detail: HistoryDetail
    }

type GrantsRequest =
    {
        ActorId: Guid
        AfterCursor: string option
        Limit: int
    }

type CaseGetRequest = { CaseReference: string }

type ObserveRequest = { OperationId: Guid }

type ResolveRequest = { OperationId: Guid }

module HistoryDetails =
    let vocabulary =
        Vocabulary.define "historyDetail" [ HistoryDetail.Summary; HistoryDetail.Full ] (function
            | HistoryDetail.Summary -> "SUMMARY"
            | HistoryDetail.Full -> "FULL")

[<RequireQualifiedAccess>]
type ReadInput =
    | CaseOperand
    | OperationOperand
    | ActorOperand
    | Page
    | Detail

type ReadDeclaration =
    {
        Endpoint: ActorEndpoint
        Inputs: ReadInput list
    }

[<NoEquality; NoComparison>]
type ReadProfile =
    {
        Text: Codec<string>
        Cursor: Codec<string>
        Limit: Codec<int>
        Detail: Codec<HistoryDetail>
    }

[<NoEquality; NoComparison>]
type ReadDefinition<'Request> =
    private
        {
            Info: ReadDeclaration
            Build: ReadProfile -> Codec<'Request>
        }

    member this.Description = this.Info
    member this.Codec profile = this.Build profile

[<NoEquality; NoComparison>]
type private ReadFields<'Request, 'Ctor> =
    {
        Inputs: ReadInput list
        Build: ReadProfile -> Fields<'Request, 'Ctor>
    }

/// Record layout and authored payloads are declared together. Boundaries choose their text profile.
module Record =
    // A flat declaration maps every closed field to its key and scalar meaning.
    // fsharplint:disable-next-line CyclomaticComplexity
    let private identity =
        function
        | BusinessField.IncidentDate -> "incidentDate", FieldValueKind.CalendarDate
        | BusinessField.IncidentNotificationDate ->
            "incidentNotificationDate", FieldValueKind.CalendarDate
        | BusinessField.IncidentCountry -> "incidentCountry", FieldValueKind.Country
        | BusinessField.ClaimantName -> "claimantName", FieldValueKind.PartyName
        | BusinessField.InsurerName -> "insurerName", FieldValueKind.PartyName
        | BusinessField.ClaimedAmount -> "claimedAmount", FieldValueKind.Amount
        | BusinessField.ClaimedCurrency -> "claimedCurrency", FieldValueKind.Currency
        | BusinessField.CaseReference -> "caseReference", FieldValueKind.Reference
        | BusinessField.PaymentDecisionDate -> "paymentDecisionDate", FieldValueKind.CalendarDate
        | BusinessField.PayableAmount -> "payableAmount", FieldValueKind.Amount
        | BusinessField.PayableCurrency -> "payableCurrency", FieldValueKind.Currency
        | BusinessField.PaymentDate -> "paymentDate", FieldValueKind.CalendarDate
        | BusinessField.Status -> "status", FieldValueKind.Status

    let key field = identity field |> fst
    let valueKind field = identity field |> snd

    let private record ctor : GroupFields<'Whole, 'Ctor> =
        {
            Members = []
            Build = fun _ -> Codec.record ctor
        }

    let private field
        name
        get
        (fields: GroupFields<'Whole, string -> 'Rest>)
        : GroupFields<'Whole, 'Rest> =
        {
            Members = fields.Members @ [ name ]
            Build = fun text -> fields.Build text |> Codec.field (key name) get text
        }

    let private finish name (fields: GroupFields<'Whole, 'Whole>) : GroupDeclaration<'Whole> =
        {
            Members = fields.Members
            Build = fun text -> fields.Build text |> Codec.build |> Codec.named name
        }

    let registration =
        (record (fun incident notification country claimant insurer amount currency ->
            {
                RegistrationInput.IncidentDate = incident
                IncidentNotificationDate = notification
                IncidentCountry = country
                ClaimantName = claimant
                InsurerName = insurer
                ClaimedAmount = amount
                ClaimedCurrency = currency
            })
        : GroupFields<RegistrationInput, _>)
        |> field BusinessField.IncidentDate _.IncidentDate
        |> field BusinessField.IncidentNotificationDate _.IncidentNotificationDate
        |> field BusinessField.IncidentCountry _.IncidentCountry
        |> field BusinessField.ClaimantName _.ClaimantName
        |> field BusinessField.InsurerName _.InsurerName
        |> field BusinessField.ClaimedAmount _.ClaimedAmount
        |> field BusinessField.ClaimedCurrency _.ClaimedCurrency
        |> finish "Registration"

    let decision =
        (record (fun date amount currency ->
            {
                DecisionInput.PaymentDecisionDate = date
                PayableAmount = amount
                PayableCurrency = currency
            })
        : GroupFields<DecisionInput, _>)
        |> field BusinessField.PaymentDecisionDate _.PaymentDecisionDate
        |> field BusinessField.PayableAmount _.PayableAmount
        |> field BusinessField.PayableCurrency _.PayableCurrency
        |> finish "Decision"

    let payment =
        (record (fun date -> { PaymentInput.PaymentDate = date }): GroupFields<PaymentInput, _>)
        |> field BusinessField.PaymentDate _.PaymentDate
        |> finish "Payment"

    let fieldsOf =
        function
        | RecordGroup.Registration -> registration.Fields
        | RecordGroup.Decision -> decision.Fields
        | RecordGroup.Payment -> payment.Fields

    let private declared group allowsAbsence field =
        {
            Field = field
            Key = key field
            Kind = valueKind field
            Group = group
            AllowsAbsence = allowsAbsence
            RetainedOnErasure = field = BusinessField.CaseReference
        }

    let fields =
        (registration.Fields |> List.map (declared (Some RecordGroup.Registration) false))
        @ [ declared None false BusinessField.CaseReference ]
        @ (decision.Fields |> List.map (declared (Some RecordGroup.Decision) true))
        @ (payment.Fields |> List.map (declared (Some RecordGroup.Payment) true))
        @ [ declared None false BusinessField.Status ]

    let private values prefill create extract =
        {
            Create = create
            Extract = extract
            Prefill = prefill
        }

    let private existing kind payload =
        {
            Kind = kind
            RevisionMode = RevisionInput.Expected
            Payload = payload
        }

    let private business kind payload =
        existing (CaseActKind.Business kind) payload

    let private unit act =
        existing (UnvalidatedCaseActs.kind act) (CasePayload.NoPayload act)

    let caseActs =
        [
            {
                Kind = CaseActKind.Business BusinessActKind.Open
                RevisionMode = RevisionInput.New
                Payload =
                    CasePayload.Registration(
                        values false UnvalidatedCaseAct.Open (function
                            | UnvalidatedCaseAct.Open value -> Some value
                            | _ -> None)
                    )
            }
            business
                BusinessActKind.AmendRegistration
                (CasePayload.Registration(
                    values true UnvalidatedCaseAct.AmendRegistration (function
                        | UnvalidatedCaseAct.AmendRegistration value -> Some value
                        | _ -> None)
                ))
            business
                BusinessActKind.Correct
                (CasePayload.Correction(
                    values true UnvalidatedCaseAct.Correct (function
                        | UnvalidatedCaseAct.Correct value -> Some value
                        | _ -> None)
                ))
            business
                BusinessActKind.Decide
                (CasePayload.Decision(
                    values true UnvalidatedCaseAct.Decide (function
                        | UnvalidatedCaseAct.Decide value -> Some value
                        | _ -> None)
                ))
            unit UnvalidatedCaseAct.WithdrawDecision
            business
                BusinessActKind.RecordPayment
                (CasePayload.Payment(
                    values false UnvalidatedCaseAct.RecordPayment (function
                        | UnvalidatedCaseAct.RecordPayment value -> Some value
                        | _ -> None)
                ))
            unit UnvalidatedCaseAct.ClearPayment
            unit UnvalidatedCaseAct.Close
            unit UnvalidatedCaseAct.Reopen
            existing
                (CaseActKind.Stewardship StewardshipActKind.Void)
                (CasePayload.Reason(
                    values false UnvalidatedCaseAct.Void (function
                        | UnvalidatedCaseAct.Void value -> Some value
                        | _ -> None)
                ))
            existing
                (CaseActKind.Stewardship StewardshipActKind.Reinstate)
                (CasePayload.Reason(
                    values false UnvalidatedCaseAct.Reinstate (function
                        | UnvalidatedCaseAct.Reinstate value -> Some value
                        | _ -> None)
                ))
            unit UnvalidatedCaseAct.Erase
        ]

    let authorityActs =
        [
            {
                Kind = AuthorityKind.Register
                Payload = AuthorityPayload.Target AuthorityAct.Register
            }
            {
                Kind = AuthorityKind.Grant
                Payload =
                    AuthorityPayload.Grant(
                        AuthorityAct.Grant,
                        (function
                        | AuthorityAct.Grant value -> Some value
                        | _ -> None)
                    )
            }
            {
                Kind = AuthorityKind.Revoke
                Payload =
                    AuthorityPayload.Grant(
                        AuthorityAct.Revoke,
                        (function
                        | AuthorityAct.Revoke value -> Some value
                        | _ -> None)
                    )
            }
            {
                Kind = AuthorityKind.Enable
                Payload = AuthorityPayload.Target AuthorityAct.Enable
            }
            {
                Kind = AuthorityKind.Disable
                Payload = AuthorityPayload.Target AuthorityAct.Disable
            }
        ]

    let private read ctor : ReadFields<'Whole, 'Ctor> =
        {
            Inputs = []
            Build = fun _ -> Codec.record ctor
        }

    let private caseOperand
        get
        (fields: ReadFields<'Whole, string -> 'Rest>)
        : ReadFields<'Whole, 'Rest> =
        {
            Inputs = fields.Inputs @ [ ReadInput.CaseOperand ]
            Build =
                fun profile ->
                    fields.Build profile
                    |> Codec.field (key BusinessField.CaseReference) get profile.Text
        }

    let private operationOperand
        get
        (fields: ReadFields<'Whole, Guid -> 'Rest>)
        : ReadFields<'Whole, 'Rest> =
        {
            Inputs = fields.Inputs @ [ ReadInput.OperationOperand ]
            Build = fun profile -> fields.Build profile |> Codec.field "operationId" get Codec.uuid
        }

    let private actorOperand
        get
        (fields: ReadFields<'Whole, Guid -> 'Rest>)
        : ReadFields<'Whole, 'Rest> =
        {
            Inputs = fields.Inputs @ [ ReadInput.ActorOperand ]
            Build = fun profile -> fields.Build profile |> Codec.field "actorId" get Codec.uuid
        }

    let private detail
        get
        (fields: ReadFields<'Whole, HistoryDetail -> 'Rest>)
        : ReadFields<'Whole, 'Rest> =
        {
            Inputs = fields.Inputs @ [ ReadInput.Detail ]
            Build = fun profile -> fields.Build profile |> Codec.field "detail" get profile.Detail
        }

    let private page
        cursor
        limit
        (fields: ReadFields<'Whole, string option -> int -> 'Rest>)
        : ReadFields<'Whole, 'Rest> =
        {
            Inputs = fields.Inputs @ [ ReadInput.Page ]
            Build =
                fun profile ->
                    fields.Build profile
                    |> Codec.optionalField "cursor" cursor profile.Cursor
                    |> Codec.field "limit" limit profile.Limit
        }

    let private finishRead
        name
        endpoint
        (fields: ReadFields<'Whole, 'Whole>)
        : ReadDefinition<'Whole> =
        {
            Info =
                {
                    Endpoint = endpoint
                    Inputs = fields.Inputs
                }
            Build = fun profile -> fields.Build profile |> Codec.build |> Codec.named name
        }

    let caseGet =
        (read (fun reference ->
            {
                CaseGetRequest.CaseReference = reference
            })
        : ReadFields<CaseGetRequest, _>)
        |> caseOperand _.CaseReference
        |> finishRead "CaseGetRequest" ActorEndpoint.CaseGet

    let caseList =
        (read (fun cursor limit ->
            {
                PageRequest.AfterCursor = cursor
                Limit = limit
            })
        : ReadFields<PageRequest, _>)
        |> page _.AfterCursor _.Limit
        |> finishRead "PageRequest" ActorEndpoint.CaseList

    let caseHistory =
        (read (fun reference detail cursor limit ->
            {
                HistoryRequest.CaseReference = reference
                Detail = detail
                AfterCursor = cursor
                Limit = limit
            })
        : ReadFields<HistoryRequest, _>)
        |> caseOperand _.CaseReference
        |> detail _.Detail
        |> page _.AfterCursor _.Limit
        |> finishRead "HistoryRequest" ActorEndpoint.CaseHistory

    let observe =
        (read (fun operation ->
            {
                ObserveRequest.OperationId = operation
            })
        : ReadFields<ObserveRequest, _>)
        |> operationOperand _.OperationId
        |> finishRead "ObserveRequest" ActorEndpoint.Observe

    let resolve =
        (read (fun operation ->
            {
                ResolveRequest.OperationId = operation
            })
        : ReadFields<ResolveRequest, _>)
        |> operationOperand _.OperationId
        |> finishRead "ResolveRequest" ActorEndpoint.Resolve

    let authorityList =
        (read (fun cursor limit ->
            {
                PageRequest.AfterCursor = cursor
                Limit = limit
            })
        : ReadFields<PageRequest, _>)
        |> page _.AfterCursor _.Limit
        |> finishRead "PageRequest" ActorEndpoint.AuthorityList

    let authorityGrants =
        (read (fun actor cursor limit ->
            {
                GrantsRequest.ActorId = actor
                AfterCursor = cursor
                Limit = limit
            })
        : ReadFields<GrantsRequest, _>)
        |> actorOperand _.ActorId
        |> page _.AfterCursor _.Limit
        |> finishRead "GrantsRequest" ActorEndpoint.AuthorityGrants

    let context = read () |> finishRead "Empty" ActorEndpoint.Context

    let reads =
        [
            caseGet.Description
            caseList.Description
            caseHistory.Description
            observe.Description
            resolve.Description
            authorityList.Description
            authorityGrants.Description
            context.Description
        ]

    /// Both boundary profiles interpret the same typed payload constructors and group members.
    let caseCodec text correction =
        let registration = registration.Codec text
        let decision = decision.Codec text
        let payment = payment.Codec text

        let values codec shape token =
            Codec.variant
                token
                shape.Create
                shape.Extract
                (Codec.record id |> Codec.field "values" id codec)

        caseActs
        |> List.map (fun declaration ->
            let token = CaseActKinds.token declaration.Kind

            match declaration.Payload with
            | CasePayload.NoPayload act -> Codec.unitVariant token act
            | CasePayload.Registration shape -> values registration shape token
            | CasePayload.Decision shape -> values decision shape token
            | CasePayload.Payment shape -> values payment shape token
            | CasePayload.Reason shape ->
                Codec.variant
                    token
                    shape.Create
                    shape.Extract
                    (Codec.record id |> Codec.field "reason" id text)
            | CasePayload.Correction shape ->
                Codec.variant
                    token
                    shape.Create
                    shape.Extract
                    (Codec.record id |> Codec.field "groups" id correction))
        |> Codec.tagged "CaseAct"

    let authorityVariants role scope =
        let grant =
            Codec.record (fun role scope ->
                {
                    GrantChange.Role = role
                    Scope = scope
                })
            |> Codec.field "role" _.Role role
            |> Codec.field "scope" _.Scope scope

        authorityActs
        |> List.map (fun declaration ->
            let token = AuthorityKinds.token declaration.Kind

            match declaration.Payload with
            | AuthorityPayload.Target act -> Codec.unitVariant token act
            | AuthorityPayload.Grant(create, extract) -> Codec.variant token create extract grant)
