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
type CaseActKind =
    | Open
    | AmendRegistration
    | Correct
    | Decide
    | WithdrawDecision
    | RecordPayment
    | ClearPayment
    | Close
    | Reopen
    | Void
    | Reinstate
    | Erase

module CaseActKind =
    let isStewardship =
        function
        | CaseActKind.Void
        | CaseActKind.Reinstate
        | CaseActKind.Erase -> true
        | CaseActKind.Open
        | CaseActKind.AmendRegistration
        | CaseActKind.Correct
        | CaseActKind.Decide
        | CaseActKind.WithdrawDecision
        | CaseActKind.RecordPayment
        | CaseActKind.ClearPayment
        | CaseActKind.Close
        | CaseActKind.Reopen -> false

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
    let vocabulary = Vocabulary.ofUnion<RecordGroup> "group" TokenCasing.CamelCase None

    let token = Vocabulary.token vocabulary

module CorrectionActions =
    let vocabulary =
        Vocabulary.ofUnion<CorrectionGroupAction> "token" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    let forGroup =
        function
        | RecordGroup.Registration -> [ CorrectionGroupAction.Keep; CorrectionGroupAction.Replace ]
        | RecordGroup.Decision
        | RecordGroup.Payment -> all

module CaseStatuses =
    let vocabulary =
        Vocabulary.ofUnion<CaseStatus> "token" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

module CaseActKinds =
    let vocabulary =
        Vocabulary.ofUnion<CaseActKind> "act" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

module AuthorityKinds =
    let vocabulary =
        Vocabulary.ofUnion<AuthorityKind> "act" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

module ActKinds =
    let vocabulary =
        [
            yield!
                Vocabulary.entries CaseActKinds.vocabulary
                |> List.map (fun (value, token) -> ActKind.Case value, token)
            yield!
                Vocabulary.entries AuthorityKinds.vocabulary
                |> List.map (fun (value, token) -> ActKind.Authority value, token)
        ]
        |> Vocabulary.ofEntries "act"

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
        | UnvalidatedCaseAct.Open _ -> CaseActKind.Open
        | UnvalidatedCaseAct.AmendRegistration _ -> CaseActKind.AmendRegistration
        | UnvalidatedCaseAct.Correct _ -> CaseActKind.Correct
        | UnvalidatedCaseAct.Decide _ -> CaseActKind.Decide
        | UnvalidatedCaseAct.WithdrawDecision -> CaseActKind.WithdrawDecision
        | UnvalidatedCaseAct.RecordPayment _ -> CaseActKind.RecordPayment
        | UnvalidatedCaseAct.ClearPayment -> CaseActKind.ClearPayment
        | UnvalidatedCaseAct.Close -> CaseActKind.Close
        | UnvalidatedCaseAct.Reopen -> CaseActKind.Reopen
        | UnvalidatedCaseAct.Void _ -> CaseActKind.Void
        | UnvalidatedCaseAct.Reinstate _ -> CaseActKind.Reinstate
        | UnvalidatedCaseAct.Erase -> CaseActKind.Erase

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

[<RequireQualifiedAccess>]
type Lifecycle =
    | Active
    | Voided
    | Erased

module Lifecycle =
    let vocabulary =
        Vocabulary.ofUnion<Lifecycle> "token" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

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

    let lifecycle =
        function
        | CaseState.Active _ -> Lifecycle.Active
        | CaseState.Voided _ -> Lifecycle.Voided
        | CaseState.Erased -> Lifecycle.Erased

    let token state = lifecycle state |> Lifecycle.token

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
        Vocabulary.ofUnion<HistoryDetail> "historyDetail" TokenCasing.ScreamingSnake None

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
    let fieldVocabulary =
        Vocabulary.ofUnion<BusinessField> "field" TokenCasing.CamelCase None

    let key = Vocabulary.token fieldVocabulary

    // Scalar meaning is an exhaustive domain declaration; compiler coverage protects every field.
    // fsharplint:disable-next-line CyclomaticComplexity
    let valueKind =
        function
        | BusinessField.IncidentDate
        | BusinessField.IncidentNotificationDate
        | BusinessField.PaymentDecisionDate
        | BusinessField.PaymentDate -> FieldValueKind.CalendarDate
        | BusinessField.IncidentCountry -> FieldValueKind.Country
        | BusinessField.ClaimantName
        | BusinessField.InsurerName -> FieldValueKind.PartyName
        | BusinessField.ClaimedAmount
        | BusinessField.PayableAmount -> FieldValueKind.Amount
        | BusinessField.ClaimedCurrency
        | BusinessField.PayableCurrency -> FieldValueKind.Currency
        | BusinessField.CaseReference -> FieldValueKind.Reference
        | BusinessField.Status -> FieldValueKind.Status

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

    let private unit act =
        existing (UnvalidatedCaseActs.kind act) (CasePayload.NoPayload act)

    let caseActs =
        [
            {
                Kind = CaseActKind.Open
                RevisionMode = RevisionInput.New
                Payload =
                    CasePayload.Registration(
                        values false UnvalidatedCaseAct.Open (function
                            | UnvalidatedCaseAct.Open value -> Some value
                            | _ -> None)
                    )
            }
            existing
                CaseActKind.AmendRegistration
                (CasePayload.Registration(
                    values true UnvalidatedCaseAct.AmendRegistration (function
                        | UnvalidatedCaseAct.AmendRegistration value -> Some value
                        | _ -> None)
                ))
            existing
                CaseActKind.Correct
                (CasePayload.Correction(
                    values true UnvalidatedCaseAct.Correct (function
                        | UnvalidatedCaseAct.Correct value -> Some value
                        | _ -> None)
                ))
            existing
                CaseActKind.Decide
                (CasePayload.Decision(
                    values true UnvalidatedCaseAct.Decide (function
                        | UnvalidatedCaseAct.Decide value -> Some value
                        | _ -> None)
                ))
            unit UnvalidatedCaseAct.WithdrawDecision
            existing
                CaseActKind.RecordPayment
                (CasePayload.Payment(
                    values false UnvalidatedCaseAct.RecordPayment (function
                        | UnvalidatedCaseAct.RecordPayment value -> Some value
                        | _ -> None)
                ))
            unit UnvalidatedCaseAct.ClearPayment
            unit UnvalidatedCaseAct.Close
            unit UnvalidatedCaseAct.Reopen
            existing
                CaseActKind.Void
                (CasePayload.Reason(
                    values false UnvalidatedCaseAct.Void (function
                        | UnvalidatedCaseAct.Void value -> Some value
                        | _ -> None)
                ))
            existing
                CaseActKind.Reinstate
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
