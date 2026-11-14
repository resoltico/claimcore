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

    let principal = CanonicalInputs.principal

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

    let private finishEndpoint name endpoint (fields: ReadFields<'request, 'request>) =
        let declaration =
            ReadCatalog.declarations |> List.find (fun value -> value.Endpoint = endpoint)

        if declaration.Inputs <> fields.Inputs then
            invalidOp "The read codec must follow its declared input contract."

        fields.Fields |> build |> named name

    let private finishRead name query fields =
        finishEndpoint name (TaskId.Query query) fields

    let caseGetRequest =
        (read (fun reference -> { CaseGetRequest.Target = reference })
        : ReadFields<CaseGetRequest, _>)
        |> caseOperand _.Target
        |> finishRead RequestSchemas.CaseGetRequest QueryTaskId.CaseGet

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
        |> finishRead RequestSchemas.AssigneeRequest QueryTaskId.CaseAssignees

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
        |> finishRead RequestSchemas.ActorListRequest QueryTaskId.PeopleList

    let accessRequestsRequest =
        (read (fun cursor limit ->
            {
                PageRequest.AfterCursor = cursor
                Limit = limit
            })
        : ReadFields<PageRequest, _>)
        |> pageWith Paging.MaximumAccessRequestCursorCharacters _.AfterCursor _.Limit
        |> finishRead RequestSchemas.AccessRequestsRequest QueryTaskId.PeopleRequests

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
        |> finishEndpoint
            RequestSchemas.AccessRequestDismissRequest
            (TaskId.Command CommandTaskId.PeopleRequestsDismiss)

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
        |> finishRead RequestSchemas.CaseListRequest QueryTaskId.CaseList

    let caseSummaryRequest =
        {
            Inputs = [ ReadInput.Worklist ]
            Fields = filterFields
        }
        |> finishRead RequestSchemas.CaseSummaryRequest QueryTaskId.CaseSummary

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
        |> finishRead "CaseTimelineRequest" QueryTaskId.CaseTimeline

    let operationGetRequest =
        (read (fun operation ->
            {
                OperationLookup.OperationId = operation
            })
        : ReadFields<OperationLookup, _>)
        |> operationOperand _.OperationId
        |> finishRead RequestSchemas.OperationGetRequest QueryTaskId.OperationGet

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
        |> finishRead RequestSchemas.GrantsRequest QueryTaskId.PeopleGrants

    let empty = read () |> finishRead RequestSchemas.Empty QueryTaskId.Context

    let peopleHistoryRequest =
        (read (fun actor cursor limit ->
            {
                PeopleHistoryRequest.TargetActorId = actor
                AfterCursor = cursor
                Limit = limit
            })
        : ReadFields<PeopleHistoryRequest, _>)
        |> actorFilter _.TargetActorId
        |> page _.AfterCursor _.Limit
        |> finishRead RequestSchemas.PeopleHistoryRequest QueryTaskId.PeopleHistory

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

    let casePaymentsRequest =
        (record (fun target cursor limit ->
            {
                CasePaymentsRequest.Target = target
                AfterCursor = cursor
                Limit = defaultArg limit PaymentPaging.DefaultLimit
            })
        : Fields<CasePaymentsRequest, _>)
        |> field "target" _.Target caseSelector
        |> optionalField "cursor" _.AfterCursor (boundedText 1 512)
        |> optionalField
            "limit"
            (fun request -> Some request.Limit)
            (integer 1L (int64 PaymentPaging.MaximumLimit) |> map int int64)
        |> build
        |> named "CasePaymentsRequest"

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
        |> field "limit" _.Limit (integer 1L (int64 NotePaging.MaximumLimit) |> map int int64)
        |> build
        |> named "CaseNotesRequest"
