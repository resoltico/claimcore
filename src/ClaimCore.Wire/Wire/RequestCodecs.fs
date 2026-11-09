namespace ClaimCore.Wire

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts

open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.RecordFormat


module RequestCodecs =
    open Codec

    let private tagged vocabulary name variants =
        Codec.taggedOn WireMembers.Kind name variants
        |> Codec.vocabulary (Vocabulary.name vocabulary)

    let revision = integer 0L JsonValue.MaximumInteger

    let reminderRevision =
        integer (Revision.value Revision.first) JsonValue.MaximumInteger

    let sequence = integer 1L JsonValue.MaximumInteger

    let registration = CanonicalInputs.registration
    let decision = CanonicalInputs.decision
    let payment = CanonicalInputs.payment
    let registrationUpdate = CanonicalInputs.registrationUpdate
    let decisionUpdate = CanonicalInputs.decisionUpdate
    let work = CanonicalInputs.work
    let workAct = CanonicalInputs.workAct
    let reminderActWith = CanonicalInputs.reminderActWith
    let noteAct = CanonicalInputs.noteAct
    let reminderAct = CanonicalInputs.reminderAct
    let registrationGroup = CanonicalInputs.registrationGroup
    let decisionGroup = CanonicalInputs.decisionGroup
    let correction = CanonicalInputs.correction
    let caseAct = CanonicalInputs.caseAct

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

    let workRequestFields =
        (record (fun operation target expected act ->
            {
                WorkOperationRequest.OperationId = operation
                Target = target
                ExpectedRevision = expected
                Act = act
            })
        : Fields<WorkOperationRequest, _>)
        |> field "operationId" _.OperationId uuid
        |> field "target" _.Target caseSelector
        |> field "expectedRevision" _.ExpectedRevision revision
        |> field "act" _.Act workAct

    let reminderRequestFields =
        (record (fun operation target act ->
            {
                ReminderOperationRequest.OperationId = operation
                Target = target
                Act = act
            })
        : Fields<ReminderOperationRequest, _>)
        |> field "operationId" _.OperationId uuid
        |> field "target" _.Target caseSelector
        |> field "act" _.Act reminderAct

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

    let principal = CanonicalInputs.principal

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

    let actorProfileInput = CanonicalInputs.actorProfileInput

    let authorityActWith role =
        CanonicalInputs.authorityActWith role scope

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

    let noteRequestFields =
        (record (fun operation target act ->
            {
                NoteOperationRequest.OperationId = operation
                Target = target
                Act = act
            })
        : Fields<NoteOperationRequest, _>)
        |> field "operationId" _.OperationId uuid
        |> field "target" _.Target caseSelector
        |> field "act" _.Act CanonicalInputs.noteAct

    let operationInput =
        tagged
            ActSubject.vocabulary
            RequestSchemas.OperationInput
            [
                variant
                    "NOTE"
                    OperationInput.Note
                    (function
                    | OperationInput.Note value -> Some value
                    | _ -> None)
                    noteRequestFields
                caseVariant
                authorityVariant
                variant
                    "WORK"
                    OperationInput.Work
                    (function
                    | OperationInput.Work value -> Some value
                    | _ -> None)
                    workRequestFields
                variant
                    "REMINDER"
                    OperationInput.Reminder
                    (function
                    | OperationInput.Reminder value -> Some value
                    | _ -> None)
                    reminderRequestFields
            ]

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

    let actorListRequest =
        let fields
            : ReadFields<
                ActorListRequest,
                string option -> Role option -> string option -> int -> ActorListRequest
               > =
            {
                Inputs = [ ReadInput.DirectorySearch; ReadInput.ActorRole ]
                Fields =
                    record (fun text role cursor limit ->
                        {
                            ActorListRequest.Text = text
                            Role = role
                            AfterCursor = cursor
                            Limit = limit
                        })
            }

        {
            Inputs = fields.Inputs
            Fields =
                fields.Fields
                |> optionalField "text" _.Text text
                |> optionalField "role" _.Role (VocabularyCodec.create Role.vocabulary)
        }
        |> pageWith Paging.MaximumActorCursorCharacters _.AfterCursor _.Limit
        |> finishRead RequestSchemas.ActorListRequest QueryEndpoint.AuthorityList

    let accessRequestsRequest =
        (read (fun cursor limit ->
            {
                PageRequest.AfterCursor = cursor
                Limit = limit
            })
        : ReadFields<PageRequest, _>)
        |> pageWith Paging.MaximumAccessRequestCursorCharacters _.AfterCursor _.Limit
        |> finishRead RequestSchemas.AccessRequestsRequest QueryEndpoint.AuthorityRequests

    let accessRequestDismissRequest =
        let payload: ReadFields<PrincipalInput, PrincipalInput> =
            {
                Inputs = [ ReadInput.PrincipalOperand ]
                Fields =
                    (record (fun kind issuer subject ->
                        {
                            PrincipalInput.Kind = kind
                            Issuer = issuer
                            Subject = subject
                        })
                    : Fields<PrincipalInput, _>)
                    |> field "kind" _.Kind (VocabularyCodec.create PrincipalKind.vocabulary)
                    |> field "issuer" _.Issuer text
                    |> field "subject" _.Subject text
            }

        payload
        |> finishRead
            RequestSchemas.AccessRequestDismissRequest
            QueryEndpoint.AuthorityRequestsDismiss

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
        (record (fun (filter: CaseListFilter<string>) assignment due view responsibleActor ->
            { filter with
                Assignment = assignment
                Due = due
                View = view
                ResponsibleActor = responsibleActor
            })
        : Fields<CaseListFilter<string>, _>)
        |> flatten id baseFilterFields
        |> optionalField
            "assignment"
            _.Assignment
            (VocabularyCodec.create CaseAssignment.vocabulary)
        |> optionalField "due" _.Due (VocabularyCodec.create CaseDue.vocabulary)
        |> optionalField "view" _.View (VocabularyCodec.create CaseView.vocabulary)
        |> optionalField
            "responsibleActor"
            _.ResponsibleActor
            (uuid |> map (ActorId.create >> Option.get) ActorId.value)

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

    let timelineRequest =
        let fields =
            (read (fun target filter cursor limit ->
                {
                    CaseTimelineRequest.Target = target
                    Filter = filter
                    AfterCursor = cursor
                    Limit = defaultArg limit Paging.DefaultPageSize
                })
            : ReadFields<CaseTimelineRequest, _>)
            |> caseOperand _.Target

        {
            Inputs = fields.Inputs @ [ ReadInput.TimelineFilter; ReadInput.Page ]
            Fields =
                fields.Fields
                |> optionalField
                    "filter"
                    _.Filter
                    (VocabularyCodec.create TimelineFilter.vocabulary)
                |> optionalField "cursor" _.AfterCursor (boundedText 1 512)
                |> optionalField
                    "limit"
                    (fun request -> Some request.Limit)
                    (integer 1L (int64 Paging.MaximumPageSize) |> map int int64)
        }
        |> finishRead "CaseTimelineRequest" QueryEndpoint.CaseTimeline

    let operationGetRequest =
        (read (fun operation ->
            {
                OperationLookup.OperationId = operation
            })
        : ReadFields<OperationLookup, _>)
        |> operationOperand _.OperationId
        |> finishRead RequestSchemas.OperationGetRequest QueryEndpoint.OperationGet

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

    let reminderListRequest =
        (record (fun target state view due assignment search cursor limit inactive ->
            {
                ReminderListRequest.Target = target
                View = view
                State = state
                Due = due
                Assignment = assignment
                Text = search
                AfterCursor = cursor
                Limit = limit
                IncludeInactive = inactive
            })
        : Fields<ReminderListRequest, _>)
        |> optionalField "target" _.Target caseSelector
        |> optionalField "state" _.State (VocabularyCodec.create ReminderState.vocabulary)
        |> optionalField "view" _.View (VocabularyCodec.create ReminderView.vocabulary)
        |> optionalField "due" _.Due (VocabularyCodec.create ReminderDue.vocabulary)
        |> optionalField
            "assignment"
            _.Assignment
            (VocabularyCodec.create CaseAssignment.vocabulary)
        |> optionalField "text" _.Text text
        |> optionalField "cursor" _.AfterCursor (boundedText 1 Paging.MaximumCaseCursorCharacters)
        |> field "limit" _.Limit (integer 1L (int64 Paging.MaximumPageSize) |> map int int64)
        |> optionalField "includeInactive" _.IncludeInactive boolean
        |> build
        |> named "ReminderListRequest"

    let reminderGetRequest =
        (record (fun target reminder ->
            {
                ReminderGetRequest.Target = target
                ReminderId = reminder
            })
        : Fields<ReminderGetRequest, _>)
        |> field "target" _.Target caseSelector
        |> field "reminderId" _.ReminderId uuid
        |> build
        |> named "ReminderGetRequest"

    let caseNotesRequest =
        (record (fun target cursor limit ->
            {
                CaseNotesRequest.Target = target
                AfterCursor = cursor
                Limit = limit
            })
        : Fields<CaseNotesRequest, _>)
        |> field "target" _.Target caseSelector
        |> optionalField "cursor" _.AfterCursor (boundedText 1 512)
        |> field "limit" _.Limit (integer 1L 20L |> map int int64)
        |> build
        |> named "CaseNotesRequest"
