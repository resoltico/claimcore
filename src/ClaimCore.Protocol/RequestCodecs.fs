namespace ClaimCore.Protocol

open System
open ClaimCore.Domain

open ClaimCore.Protocol
open ClaimCore.Json


module RequestCodecs =
    open Codec

    let revision = integer 0L JsonValue.MaximumInteger
    let sequence = integer 1L JsonValue.MaximumInteger

    let tokens values =
        values |> List.map (fun value -> value, value) |> enumeration

    let private inputField name get fields =
        Codec.field (BusinessField.key name) get Codec.text fields

    let registration =
        (Codec.record (fun incident notification country claimant insurer amount currency ->
            {
                RegistrationInput.IncidentDate = incident
                IncidentNotificationDate = notification
                IncidentCountry = country
                ClaimantName = claimant
                InsurerName = insurer
                ClaimedAmount = amount
                ClaimedCurrency = currency
            })
        : Fields<RegistrationInput, _>)
        |> inputField BusinessField.IncidentDate _.IncidentDate
        |> inputField BusinessField.IncidentNotificationDate _.IncidentNotificationDate
        |> inputField BusinessField.IncidentCountry _.IncidentCountry
        |> inputField BusinessField.ClaimantName _.ClaimantName
        |> inputField BusinessField.InsurerName _.InsurerName
        |> inputField BusinessField.ClaimedAmount _.ClaimedAmount
        |> inputField BusinessField.ClaimedCurrency _.ClaimedCurrency
        |> Codec.build
        |> Codec.named "Registration"

    let decision =
        (Codec.record (fun date amount currency ->
            {
                DecisionInput.PaymentDecisionDate = date
                PayableAmount = amount
                PayableCurrency = currency
            })
        : Fields<DecisionInput, _>)
        |> inputField BusinessField.PaymentDecisionDate _.PaymentDecisionDate
        |> inputField BusinessField.PayableAmount _.PayableAmount
        |> inputField BusinessField.PayableCurrency _.PayableCurrency
        |> Codec.build
        |> Codec.named "Decision"

    let payment =
        (Codec.record (fun date -> { PaymentInput.PaymentDate = date }): Fields<PaymentInput, _>)
        |> inputField BusinessField.PaymentDate _.PaymentDate
        |> Codec.build
        |> Codec.named "Payment"

    let registrationGroup =
        tagged
            "RegistrationGroup"
            [
                unitVariant (CorrectionGroupAction.token CorrectionGroupAction.Keep) Amendment.Keep
                variant
                    (CorrectionGroupAction.token CorrectionGroupAction.Replace)
                    Amendment.Replace
                    (function
                    | Amendment.Replace value -> Some value
                    | _ -> None)
                    (record id |> field "values" id registration)
            ]

    let decisionGroup =
        tagged
            "DecisionGroup"
            [
                unitVariant (CorrectionGroupAction.token CorrectionGroupAction.Keep) Change.Keep
                variant
                    (CorrectionGroupAction.token CorrectionGroupAction.Replace)
                    Change.Replace
                    (function
                    | Change.Replace value -> Some value
                    | _ -> None)
                    (record id |> field "values" id decision)
                unitVariant (CorrectionGroupAction.token CorrectionGroupAction.Clear) Change.Clear
            ]

    let paymentGroup =
        tagged
            "PaymentGroup"
            [
                unitVariant (CorrectionGroupAction.token CorrectionGroupAction.Keep) Change.Keep
                variant
                    (CorrectionGroupAction.token CorrectionGroupAction.Replace)
                    Change.Replace
                    (function
                    | Change.Replace value -> Some value
                    | _ -> None)
                    (record id |> field "values" id payment)
                unitVariant (CorrectionGroupAction.token CorrectionGroupAction.Clear) Change.Clear
            ]

    let correction =
        (record (fun v0 v1 v2 ->
            {
                AuthoredCorrection.Registration = v0
                Decision = v1
                Payment = v2
            })
        : Fields<AuthoredCorrection, _>)
        |> field "registration" _.Registration (registrationGroup)
        |> field "decision" _.Decision (decisionGroup)
        |> field "payment" _.Payment (paymentGroup)
        |> build
        |> named "Correction"

    let private caseCodec token (registration, decision, payment) (reason, correction) =
        CaseActKind.all
        |> List.map (fun kind ->
            let tag = token kind

            let memberCodec name codec (inject, project) =
                variant tag inject project (record id |> field name id codec)

            match CaseAct.slot kind with
            | CaseActSlot.Bare act -> unitVariant tag act
            | CaseActSlot.Registration(inject, project) ->
                memberCodec "values" registration (inject, project)
            | CaseActSlot.Decision(inject, project) ->
                memberCodec "values" decision (inject, project)
            | CaseActSlot.Payment(inject, project) ->
                memberCodec "values" payment (inject, project)
            | CaseActSlot.Reason(inject, project) -> memberCodec "reason" reason (inject, project)
            | CaseActSlot.Correction(inject, project) ->
                memberCodec "groups" correction (inject, project))
        |> tagged "CaseAct"

    let caseActWithNames names =
        caseCodec names (registration, decision, payment) (text, correction)

    let caseAct = caseActWithNames CaseActKind.token

    let caseRequestFields =
        (record (fun v0 v1 v2 v3 ->
            {
                UnvalidatedCaseRequest.OperationId = v0
                CaseReference = v1
                ExpectedRevision = v2
                Act = v3
            })
        : Fields<UnvalidatedCaseRequest, _>)
        |> field "operationId" _.OperationId (uuid)
        |> field (BusinessField.key BusinessField.CaseReference) _.CaseReference (text)
        |> field "expectedRevision" _.ExpectedRevision (revision)
        |> field "act" _.Act (caseAct)

    let caseRequest = caseRequestFields |> build |> named "CaseRequest"

    let principal =
        (record (fun v0 v1 v2 ->
            {
                Principal.Kind = v0
                Issuer = v1
                Subject = v2
            })
        : Fields<Principal, _>)
        |> field "kind" _.Kind (tokens PrincipalKind.all)
        |> field "issuer" _.Issuer (text)
        |> field "subject" _.Subject (text)
        |> build
        |> named "Principal"

    let scope =
        tagged
            "Scope"
            [
                unitVariant (ScopeKind.token ScopeKind.Installation) GrantTarget.Installation
                variant
                    (ScopeKind.token ScopeKind.Case)
                    GrantTarget.CaseReference
                    (function
                    | GrantTarget.CaseReference value -> Some value
                    | _ -> None)
                    (record id |> field (BusinessField.key BusinessField.CaseReference) id text)
            ]

    let private authorityVariantsWith role scope =
        let grant =
            record (fun role scope ->
                {
                    GrantChange.Role = role
                    Scope = scope
                })
            |> field "role" _.Role role
            |> field "scope" _.Scope scope

        AuthorityKind.all
        |> List.filter ((<>) AuthorityKind.ProvisionOwner)
        |> List.map (fun kind ->
            let token = AuthorityKind.token kind

            match AuthorityAct.slot kind with
            | AuthorityActSlot.Bare act -> unitVariant token act
            | AuthorityActSlot.Grant(inject, project) -> variant token inject project grant)

    let authorityActWith role =
        authorityVariantsWith role scope |> tagged "AuthorityAct"

    let authorityAct = authorityActWith (tokens (Role.all |> List.map Role.token))

    let authorityRequestFields =
        (record (fun v0 v1 v2 ->
            {
                AuthorityRequest.OperationId = v0
                Target = v1
                Act = v2
            })
        : Fields<AuthorityRequest, _>)
        |> field "operationId" _.OperationId (uuid)
        |> field "target" _.Target (principal)
        |> field "act" _.Act (authorityAct)

    let authorityRequest = authorityRequestFields |> build |> named "AuthorityRequest"

    let private caseVariant =
        variant
            (ActSubject.token ActSubject.Case)
            OperationInput.Case
            (function
            | OperationInput.Case value -> Some value
            | _ -> None)
            caseRequestFields

    let private authorityVariant =
        variant
            (ActSubject.token ActSubject.Authority)
            OperationInput.Authority
            (function
            | OperationInput.Authority value -> Some value
            | _ -> None)
            authorityRequestFields

    let operationInput = tagged "OperationInput" [ caseVariant; authorityVariant ]

    [<NoEquality; NoComparison>]
    type private ReadFields<'request, 'ctor> =
        {
            Inputs: ReadInput list
            Fields: Fields<'request, 'ctor>
        }

    let private read ctor : ReadFields<'request, 'ctor> = { Inputs = []; Fields = record ctor }

    let private caseOperand get (fields: ReadFields<'request, string -> 'rest>) =
        {
            Inputs = fields.Inputs @ [ ReadInput.CaseOperand ]
            Fields = fields.Fields |> field (BusinessField.key BusinessField.CaseReference) get text
        }

    let private operationOperand get (fields: ReadFields<'request, Guid -> 'rest>) =
        {
            Inputs = fields.Inputs @ [ ReadInput.OperationOperand ]
            Fields = fields.Fields |> field "operationId" get uuid
        }

    let private actorOperand get (fields: ReadFields<'request, Guid -> 'rest>) =
        {
            Inputs = fields.Inputs @ [ ReadInput.ActorOperand ]
            Fields = fields.Fields |> field "actorId" get uuid
        }

    let private detail get (fields: ReadFields<'request, HistoryDetail -> 'rest>) =
        {
            Inputs = fields.Inputs @ [ ReadInput.Detail ]
            Fields =
                fields.Fields
                |> field "detail" get (VocabularyCodec.create HistoryDetail.vocabulary)
        }

    let private page cursor limit (fields: ReadFields<'request, string option -> int -> 'rest>) =
        {
            Inputs = fields.Inputs @ [ ReadInput.Page ]
            Fields =
                fields.Fields
                |> optionalField "cursor" cursor (boundedText 1 512)
                |> field "limit" limit (integer 1L (int64 Paging.MaximumPageSize) |> map int int64)
        }

    let private finishRead name endpoint (fields: ReadFields<'request, 'request>) =
        let declaration =
            ReadCatalog.declarations |> List.find (fun value -> value.Endpoint = endpoint)

        if declaration.Inputs <> fields.Inputs then
            invalidOp "The read codec must follow its Domain input declaration."

        fields.Fields |> build |> named name

    let caseGetRequest =
        (read (fun reference ->
            {
                CaseGetRequest.CaseReference = reference
            })
        : ReadFields<CaseGetRequest, _>)
        |> caseOperand _.CaseReference
        |> finishRead "CaseGetRequest" QueryEndpoint.CaseGet

    let pageRequest =
        (read (fun cursor limit ->
            {
                PageRequest.AfterCursor = cursor
                Limit = limit
            })
        : ReadFields<PageRequest, _>)
        |> page _.AfterCursor _.Limit
        |> finishRead "PageRequest" QueryEndpoint.CaseList

    let historyRequest =
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
        |> finishRead "HistoryRequest" QueryEndpoint.CaseHistory

    let observeRequest =
        (read (fun operation ->
            {
                ObserveRequest.OperationId = operation
            })
        : ReadFields<ObserveRequest, _>)
        |> operationOperand _.OperationId
        |> finishRead "ObserveRequest" QueryEndpoint.Observe

    let resolveRequest =
        (read (fun operation ->
            {
                ResolveRequest.OperationId = operation
            })
        : ReadFields<ResolveRequest, _>)
        |> operationOperand _.OperationId
        |> finishRead "ResolveRequest" QueryEndpoint.Resolve

    let grantsRequest =
        (read (fun actor cursor limit ->
            {
                GrantsRequest.ActorId = actor
                AfterCursor = cursor
                Limit = limit
            })
        : ReadFields<GrantsRequest, _>)
        |> actorOperand _.ActorId
        |> page _.AfterCursor _.Limit
        |> finishRead "GrantsRequest" QueryEndpoint.AuthorityGrants

    let empty = read () |> finishRead "Empty" QueryEndpoint.Context
