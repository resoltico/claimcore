namespace ClaimCore.Protocol

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts

open ClaimCore.Protocol
open ClaimCore.Json


module RequestCodecs =
    open Codec

    let private tagged vocabulary name variants =
        Codec.taggedOn WireMembers.Kind name variants
        |> Codec.vocabulary (Vocabulary.name vocabulary)

    let revision = integer 0L JsonValue.MaximumInteger
    let sequence = integer 1L JsonValue.MaximumInteger

    let private inputGroup group create values =
        CaseFields.ofGroup group
        |> List.map (fun field -> field, BusinessField.key field, Codec.text)
        |> Codec.fieldMap
        |> Codec.mapResult
            (fun path fields ->
                create (Map.toList fields)
                |> Result.mapError (fun reason -> { Path = path; Reason = reason }))
            (values >> Map.ofList)

    let registration =
        inputGroup RecordGroup.Registration RegistrationInput.create RegistrationInput.values
        |> Codec.named "Registration"

    let decision =
        inputGroup RecordGroup.Decision DecisionInput.create DecisionInput.values
        |> Codec.named "Decision"

    let payment =
        inputGroup RecordGroup.Payment PaymentInput.create PaymentInput.values
        |> Codec.named "Payment"


    let private moneyInput =
        (record (fun amount currency ->
            {
                MoneyInput.Amount = amount
                Currency = currency
            })
        : Fields<MoneyInput, _>)
        |> field "amount" _.Amount text
        |> field "currency" _.Currency text
        |> build

    let registrationUpdate =
        (record (fun incident notification country claimant insurer claimed ->
            {
                RegistrationUpdateInput.IncidentDate = incident
                NotificationDate = notification
                Country = country
                Claimant = claimant
                Insurer = insurer
                Claimed = claimed
            })
        : Fields<RegistrationUpdateInput, _>)
        |> optionalField "incidentDate" _.IncidentDate text
        |> optionalField "incidentNotificationDate" _.NotificationDate text
        |> optionalField "incidentCountry" _.Country text
        |> optionalField "claimantName" _.Claimant text
        |> optionalField "insurerName" _.Insurer text
        |> optionalField "claimed" _.Claimed moneyInput
        |> build
        |> named "RegistrationUpdate"

    let decisionUpdate =
        (record (fun date payable ->
            {
                DecisionUpdateInput.Date = date
                Payable = payable
            })
        : Fields<DecisionUpdateInput, _>)
        |> optionalField "paymentDecisionDate" _.Date text
        |> optionalField "payable" _.Payable moneyInput
        |> build
        |> named "DecisionUpdate"

    let private workValue name =
        Codec.taggedOn
            "kind"
            name
            [
                unitVariant
                    (Vocabulary.token WorkChoice.vocabulary WorkChoice.Clear)
                    WorkValue.Clear
                variant
                    (Vocabulary.token WorkChoice.vocabulary WorkChoice.Set)
                    WorkValue.Set
                    (function
                    | WorkValue.Set value -> Some value
                    | _ -> None)
                    (record id |> field "value" id text)
            ]
        |> Codec.vocabulary (Vocabulary.name WorkChoice.vocabulary)

    let work =
        let supplied =
            function
            | WorkValue.Keep -> None
            | choice -> Some choice

        (record (fun actor date ->
            {
                CaseWorkInput.Responsible = defaultArg actor WorkValue.Keep
                FollowUp = defaultArg date WorkValue.Keep
            })
        : Fields<CaseWorkInput, _>)
        |> optionalField
            "responsibleActor"
            (_.Responsible >> supplied)
            (workValue "ResponsibleActorChange")
        |> optionalField "followUpDate" (_.FollowUp >> supplied) (workValue "FollowUpChange")
        |> build
        |> named "CaseWorkUpdate"

    let registrationGroup =
        tagged
            CorrectionGroupAction.vocabulary
            RequestSchemas.RegistrationGroup
            [
                unitVariant (CorrectionGroupAction.token CorrectionGroupAction.Keep) Amendment.Keep
                variant
                    (CorrectionGroupAction.token CorrectionGroupAction.Update)
                    Amendment.Update
                    (function
                    | Amendment.Update value -> Some value
                    | _ -> None)
                    (record id |> field WireMembers.Values id registrationUpdate)
            ]

    let decisionGroup =
        tagged
            CorrectionGroupAction.vocabulary
            RequestSchemas.DecisionGroup
            [
                unitVariant (CorrectionGroupAction.token CorrectionGroupAction.Keep) Change.Keep
                variant
                    (CorrectionGroupAction.token CorrectionGroupAction.Update)
                    Change.Update
                    (function
                    | Change.Update value -> Some value
                    | _ -> None)
                    (record id |> field WireMembers.Values id decisionUpdate)
                unitVariant (CorrectionGroupAction.token CorrectionGroupAction.Clear) Change.Clear
            ]

    let paymentGroup =
        tagged
            CorrectionGroupAction.vocabulary
            RequestSchemas.PaymentGroup
            [
                unitVariant
                    (CorrectionGroupAction.token CorrectionGroupAction.Keep)
                    PaymentChange.Keep
                variant
                    (CorrectionGroupAction.token CorrectionGroupAction.Replace)
                    PaymentChange.Replace
                    (function
                    | PaymentChange.Replace value -> Some value
                    | _ -> None)
                    (record id |> field WireMembers.Values id payment)
                unitVariant "REAFFIRM" PaymentChange.Reaffirm
                unitVariant
                    (CorrectionGroupAction.token CorrectionGroupAction.Clear)
                    PaymentChange.Clear
            ]

    let correction =
        (record (fun v0 v1 v2 ->
            {
                AuthoredCorrection.Registration = v0
                Decision = v1
                Payment = v2
            })
        : Fields<AuthoredCorrection, _>)
        |> field (RecordGroup.token RecordGroup.Registration) _.Registration (registrationGroup)
        |> field (RecordGroup.token RecordGroup.Decision) _.Decision (decisionGroup)
        |> field (RecordGroup.token RecordGroup.Payment) _.Payment (paymentGroup)
        |> build
        |> named RequestSchemas.Correction

    let private caseCodec token (registration, decision, payment) (reason, correction) =
        CaseActKind.all
        |> List.map (fun kind ->
            let tag = token kind

            let memberCodec name codec (inject, project) =
                variant tag inject project (record id |> field name id codec)

            match CaseAct.slot kind with
            | CaseActSlot.Bare act -> unitVariant tag act
            | CaseActSlot.Registration(inject, project) ->
                memberCodec WireMembers.Values registration (inject, project)
            | CaseActSlot.RegistrationUpdate(inject, project) ->
                memberCodec "values" registrationUpdate (inject, project)
            | CaseActSlot.Work(inject, project) -> memberCodec "values" work (inject, project)
            | CaseActSlot.Decision(inject, project) ->
                memberCodec WireMembers.Values decision (inject, project)
            | CaseActSlot.Payment(inject, project) ->
                memberCodec WireMembers.Values payment (inject, project)
            | CaseActSlot.Reason(inject, project) ->
                memberCodec WireMembers.Reason reason (inject, project)
            | CaseActSlot.Correction(inject, project) ->
                memberCodec WireMembers.Groups correction (inject, project))
        |> tagged CaseActKind.vocabulary RequestSchemas.CaseAct

    let caseActWithNames names =
        caseCodec names (registration, decision, payment) (text, correction)

    let caseAct = caseActWithNames CaseActKind.token

    let caseSelector =
        let caseId = uuid |> map (CaseId.create >> Option.get) CaseId.value

        Codec.tagged
            RequestSchemas.CaseSelector
            [
                variant
                    "REFERENCE"
                    CaseSelector.Reference
                    (function
                    | CaseSelector.Reference reference -> Some reference
                    | _ -> None)
                    (record id |> field "caseReference" id text)
                variant
                    "ID"
                    CaseSelector.Id
                    (function
                    | CaseSelector.Id value -> Some value
                    | _ -> None)
                    (record id |> field "caseId" id caseId)
            ]
        |> Codec.vocabulary "caseSelector"

    let caseRequestFields =
        (record (fun v0 v1 v2 v3 ->
            {
                CaseOperationRequest.OperationId = v0
                Target = v1
                ExpectedRevision = v2
                Act = v3
            })
        : Fields<CaseOperationRequest, _>)
        |> field WireMembers.OperationId _.OperationId (uuid)
        |> field "target" _.Target caseSelector
        |> field WireMembers.ExpectedRevision _.ExpectedRevision (revision)
        |> field WireMembers.Act _.Act (caseAct)

    let caseRequest = caseRequestFields |> build |> named RequestSchemas.CaseRequest

    let principal =
        (record (fun v0 v1 v2 ->
            {
                PrincipalInput.Kind = v0
                Issuer = v1
                Subject = v2
            })
        : Fields<PrincipalInput, _>)
        |> field WireMembers.Kind _.Kind (VocabularyCodec.create PrincipalKind.vocabulary)
        |> field WireMembers.Issuer _.Issuer (text)
        |> field WireMembers.Subject _.Subject (text)
        |> build
        |> named RequestSchemas.PrincipalInput

    let scope =
        tagged
            ScopeKind.vocabulary
            RequestSchemas.Scope
            [
                unitVariant (ScopeKind.token ScopeKind.Installation) GrantTarget.Installation
                variant
                    (ScopeKind.token ScopeKind.Case)
                    (function
                    | CaseSelector.Reference reference -> GrantTarget.CaseReference reference
                    | CaseSelector.Id id -> GrantTarget.CaseId id)
                    (function
                    | GrantTarget.CaseReference reference -> Some(CaseSelector.Reference reference)
                    | GrantTarget.CaseId id -> Some(CaseSelector.Id id)
                    | _ -> None)
                    (record id |> field "target" id caseSelector)
            ]

    let private authorityVariantsWith role scope =
        let grant =
            record (fun role scope ->
                {
                    AuthoredGrant.Role = role
                    Scope = scope
                })
            |> field WireMembers.Role _.Role role
            |> field WireMembers.Scope _.Scope scope

        [
            unitVariant (AuthorityKind.token AuthorityKind.Register) AuthorityAct.Register
            variant
                (AuthorityKind.token AuthorityKind.Grant)
                AuthorityAct.Grant
                (function
                | AuthorityAct.Grant value -> Some value
                | _ -> None)
                grant
            variant
                (AuthorityKind.token AuthorityKind.Revoke)
                AuthorityAct.Revoke
                (function
                | AuthorityAct.Revoke value -> Some value
                | _ -> None)
                grant
            unitVariant (AuthorityKind.token AuthorityKind.Enable) AuthorityAct.Enable
            unitVariant (AuthorityKind.token AuthorityKind.Disable) AuthorityAct.Disable
        ]

    let authorityActWith role =
        authorityVariantsWith role scope
        |> tagged AuthorityKind.vocabulary RequestSchemas.AuthorityAct

    let authorityAct = authorityActWith (VocabularyCodec.create Role.vocabulary)

    let authorityRequestFields =
        (record (fun v0 v1 v2 ->
            {
                AuthorityRequest.OperationId = v0
                Target = v1
                Act = v2
            })
        : Fields<AuthorityRequest, _>)
        |> field WireMembers.OperationId _.OperationId (uuid)
        |> field WireMembers.Target _.Target (principal)
        |> field WireMembers.Act _.Act (authorityAct)

    let authorityRequest =
        authorityRequestFields |> build |> named RequestSchemas.AuthorityRequest

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

    let operationInput =
        tagged ActSubject.vocabulary RequestSchemas.OperationInput [ caseVariant; authorityVariant ]

    [<NoEquality; NoComparison>]
    type private ReadFields<'request, 'ctor> =
        {
            Inputs: ReadInput list
            Fields: Fields<'request, 'ctor>
        }

    let private read ctor : ReadFields<'request, 'ctor> = { Inputs = []; Fields = record ctor }

    let private caseOperand get (fields: ReadFields<'request, CaseSelector -> 'rest>) =
        {
            Inputs = fields.Inputs @ [ ReadInput.CaseOperand ]
            Fields = fields.Fields |> field "target" get caseSelector
        }

    let private operationOperand get (fields: ReadFields<'request, Guid -> 'rest>) =
        {
            Inputs = fields.Inputs @ [ ReadInput.OperationOperand ]
            Fields = fields.Fields |> field WireMembers.OperationId get uuid
        }

    let private actorOperand get (fields: ReadFields<'request, Guid -> 'rest>) =
        {
            Inputs = fields.Inputs @ [ ReadInput.ActorOperand ]
            Fields = fields.Fields |> field WireMembers.ActorId get uuid
        }

    let private actorFilter get (fields: ReadFields<'request, Guid option -> 'rest>) =
        {
            Inputs = fields.Inputs @ [ ReadInput.ActorFilter ]
            Fields = fields.Fields |> optionalField WireMembers.ActorId get uuid
        }

    let private detail get (fields: ReadFields<'request, HistoryDetail -> 'rest>) =
        {
            Inputs = fields.Inputs @ [ ReadInput.Detail ]
            Fields =
                fields.Fields
                |> field WireMembers.Detail get (VocabularyCodec.create HistoryDetail.vocabulary)
        }

    let private pageWith
        maximum
        cursor
        limit
        (fields: ReadFields<'request, string option -> int -> 'rest>)
        =
        {
            Inputs = fields.Inputs @ [ ReadInput.Page ]
            Fields =
                fields.Fields
                |> optionalField WireMembers.Cursor cursor (boundedText 1 maximum)
                |> field
                    WireMembers.Limit
                    limit
                    (integer 1L (int64 Paging.MaximumPageSize) |> map int int64)
        }

    let private page cursor limit fields = pageWith 512 cursor limit fields

    let private finishRead name endpoint (fields: ReadFields<'request, 'request>) =
        let declaration =
            ReadCatalog.declarations |> List.find (fun value -> value.Endpoint = endpoint)

        if declaration.Inputs <> fields.Inputs then
            invalidOp "The read codec must follow its declared input contract."

        fields.Fields |> build |> named name

    let caseGetRequest =
        (read (fun reference -> { CaseGetRequest.Target = reference })
        : ReadFields<CaseGetRequest, _>)
        |> caseOperand _.Target
        |> finishRead RequestSchemas.CaseGetRequest QueryEndpoint.CaseGet

    let assigneeRequest =
        (read (fun target cursor limit ->
            {
                AssigneeRequest.Target = target
                AfterCursor = cursor
                Limit = limit
            })
        : ReadFields<AssigneeRequest, _>)
        |> caseOperand _.Target
        |> page _.AfterCursor _.Limit
        |> finishRead RequestSchemas.AssigneeRequest QueryEndpoint.CaseAssignees

    let private pageFields =
        (read (fun cursor limit ->
            {
                PageRequest.AfterCursor = cursor
                Limit = limit
            })
        : ReadFields<PageRequest, _>)
        |> page _.AfterCursor _.Limit

    let pageRequest =
        pageFields |> finishRead RequestSchemas.PageRequest QueryEndpoint.AuthorityList

    let private optionalChoices name get vocabulary fields =
        fields
        |> optionalField
            name
            (get
             >> function
                 | [] -> None
                 | values -> Some values)
            (list (Vocabulary.all vocabulary |> List.length) (VocabularyCodec.create vocabulary))

    let private baseFilterFields =
        (record (fun lifecycles statuses progress text order ->
            CaseListFilter.create
                (defaultArg lifecycles [])
                (defaultArg statuses [])
                (defaultArg progress [])
                text
                (defaultArg order CaseListOrder.Reference))
        : Fields<CaseListFilter<string>, _>)
        |> optionalChoices "lifecycle" _.Lifecycles Lifecycle.vocabulary
        |> optionalChoices "status" _.Statuses CaseStatus.vocabulary
        |> optionalChoices "progress" _.Progress CaseProgress.vocabulary
        |> optionalField "text" _.Text text
        |> optionalField
            "order"
            (fun value ->
                if value.Order = CaseListOrder.Reference then
                    None
                else
                    Some value.Order)
            (VocabularyCodec.create CaseListOrder.vocabulary)

    let private filterFields =
        (record (fun (filter: CaseListFilter<string>) assignment due ->
            { filter with
                Assignment = assignment
                Due = due
            })
        : Fields<CaseListFilter<string>, _>)
        |> flatten id baseFilterFields
        |> optionalField
            "assignment"
            _.Assignment
            (VocabularyCodec.create CaseAssignment.vocabulary)
        |> optionalField "due" _.Due (VocabularyCodec.create CaseDue.vocabulary)

    let caseListRequest =
        let paging =
            (read (fun cursor limit ->
                {
                    PageRequest.AfterCursor = cursor
                    Limit = limit
                })
            : ReadFields<PageRequest, _>)
            |> pageWith Paging.MaximumCaseCursorCharacters _.AfterCursor _.Limit

        let fields =
            (record (fun page filter ->
                {
                    CaseListRequest.Page = page
                    Filter = filter
                })
            : Fields<CaseListRequest, _>)
            |> flatten _.Page paging.Fields
            |> flatten _.Filter filterFields

        {
            Inputs = paging.Inputs @ [ ReadInput.Worklist ]
            Fields = fields
        }
        |> finishRead RequestSchemas.CaseListRequest QueryEndpoint.CaseList

    let caseSummaryRequest =
        {
            Inputs = [ ReadInput.Worklist ]
            Fields = filterFields
        }
        |> finishRead RequestSchemas.CaseSummaryRequest QueryEndpoint.CaseSummary

    let historyRequest =
        (read (fun reference detail cursor limit ->
            {
                HistoryRequest.Target = reference
                Detail = detail
                AfterCursor = cursor
                Limit = limit
            })
        : ReadFields<HistoryRequest, _>)
        |> caseOperand _.Target
        |> detail _.Detail
        |> page _.AfterCursor _.Limit
        |> finishRead RequestSchemas.HistoryRequest QueryEndpoint.CaseHistory

    let observeRequest =
        (read (fun operation ->
            {
                OperationLookup.OperationId = operation
            })
        : ReadFields<OperationLookup, _>)
        |> operationOperand _.OperationId
        |> finishRead RequestSchemas.ObserveRequest QueryEndpoint.Observe

    let resolveRequest =
        (read (fun operation ->
            {
                OperationLookup.OperationId = operation
            })
        : ReadFields<OperationLookup, _>)
        |> operationOperand _.OperationId
        |> finishRead RequestSchemas.ResolveRequest QueryEndpoint.Resolve

    let statusRequest =
        (read (fun operation ->
            {
                OperationLookup.OperationId = operation
            })
        : ReadFields<OperationLookup, _>)
        |> operationOperand _.OperationId
        |> finishRead RequestSchemas.StatusRequest QueryEndpoint.Status

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
        |> finishRead RequestSchemas.GrantsRequest QueryEndpoint.AuthorityGrants

    let empty = read () |> finishRead RequestSchemas.Empty QueryEndpoint.Context

    let authorityHistoryRequest =
        (read (fun actor cursor limit ->
            {
                AuthorityHistoryRequest.TargetActorId = actor
                AfterCursor = cursor
                Limit = limit
            })
        : ReadFields<AuthorityHistoryRequest, _>)
        |> actorFilter _.TargetActorId
        |> page _.AfterCursor _.Limit
        |> finishRead RequestSchemas.AuthorityHistoryRequest QueryEndpoint.AuthorityHistory
