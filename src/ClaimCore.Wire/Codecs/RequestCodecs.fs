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

    let caseId = uuid |> map (CaseId.create >> Option.get) CaseId.value
    let actorId = uuid |> map (ActorId.create >> Option.get) ActorId.value

    let operationId =
        uuid
        |> restrict
            (Schema.String(
                Some
                    "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-7[0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$",
                Some "uuid",
                Some 36,
                Some 36,
                None
            ))
            (fun path value ->
                uuid.Read path value
                |> Result.bind (fun id ->
                    if OperationId.create id |> Option.isSome then
                        Ok()
                    else
                        Error
                            {
                                Path = path
                                Reason = "Expected an RFC UUIDv7."
                            }))
        |> map (OperationId.create >> Option.get) OperationId.value

    let caseSelector =
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

    let principal =
        PrincipalCodecs.key
        |> Codec.map PrincipalKey.toInput (fun value ->
            PrincipalKey.ofKind value.Kind value.Issuer value.Subject
            |> Result.toOption
            |> Option.get)

    let private caseOperand get fields =
        fields |> field "target" get caseSelector

    let private operationOperand get fields =
        fields |> field WireMembers.OperationId get uuid

    let private actorOperand get fields =
        fields |> field WireMembers.ActorId get uuid

    let private actorFilter get fields =
        fields |> optionalField WireMembers.ActorId get uuid

    let private pageWith maximum cursor limit fields =
        fields
        |> optionalField WireMembers.Cursor cursor (boundedText 1 maximum)
        |> field
            WireMembers.Limit
            limit
            (integer 1L (int64 Paging.MaximumPageSize) |> map int int64)

    let private page cursor limit fields =
        pageWith Paging.MaximumActorCursorCharacters cursor limit fields

    let private finishRead name fields = fields |> build |> named name

    let caseGetRequest =
        (record (fun reference -> { CaseGetRequest.Target = reference }): Fields<CaseGetRequest, _>)
        |> caseOperand _.Target
        |> finishRead RequestSchemas.CaseGetRequest

    let caseLookupRequest =
        (record (fun target cursor limit ->
            {
                CaseLookupRequest.Target = target
                AfterCursor = cursor
                Limit = limit
            })
        : Fields<CaseLookupRequest, _>)
        |> caseOperand _.Target
        |> page _.AfterCursor _.Limit
        |> finishRead RequestSchemas.CaseLookupRequest

    let actorListRequest =
        (record (fun text role cursor limit ->
            {
                ActorListRequest.Text = text
                Role = role
                AfterCursor = cursor
                Limit = limit
            })
        : Fields<ActorListRequest, _>)
        |> optionalField "text" _.Text text
        |> optionalField "role" _.Role (VocabularyCodec.create Role.vocabulary)
        |> pageWith Paging.MaximumActorCursorCharacters _.AfterCursor _.Limit
        |> finishRead RequestSchemas.ActorListRequest

    let accessRequestsRequest =
        (record (fun cursor limit ->
            {
                PageRequest.AfterCursor = cursor
                Limit = limit
            })
        : Fields<PageRequest, _>)
        |> pageWith Paging.MaximumAccessRequestCursorCharacters _.AfterCursor _.Limit
        |> finishRead RequestSchemas.AccessRequestsRequest

    let disclosureTrailRequest =
        (record (fun actor caseId kind from until cursor limit ->
            {
                DisclosureTrailRequest.ActorId = actor
                CaseId = caseId
                Kind = kind
                From = from
                Until = until
                AfterCursor = cursor
                Limit = limit
            })
        : Fields<DisclosureTrailRequest, _>)
        |> optionalField "actorId" _.ActorId actorId
        |> optionalField "caseId" _.CaseId caseId
        |> optionalField "kind" _.Kind (VocabularyCodec.create DisclosureKind.vocabulary)
        |> optionalField "from" _.From instant
        |> optionalField "until" _.Until instant
        |> pageWith Paging.MaximumDisclosureTrailCursorCharacters _.AfterCursor _.Limit
        |> finishRead "DisclosureTrailRequest"

    let accessRequestDismissRequest =
        (record (fun kind issuer subject ->
            {
                PrincipalInput.Kind = kind
                Issuer = issuer
                Subject = subject
            })
        : Fields<PrincipalInput, _>)
        |> field
            (TaskInputId.token (TaskInputId.Target InputTarget.PrincipalKind))
            _.Kind
            (VocabularyCodec.create PrincipalKind.vocabulary)
        |> field (TaskInputId.token (TaskInputId.Target InputTarget.PrincipalIssuer)) _.Issuer text
        |> field
            (TaskInputId.token (TaskInputId.Target InputTarget.PrincipalSubject))
            _.Subject
            text
        |> finishRead RequestSchemas.AccessRequestDismissRequest

    let private terminationIdentity get fields =
        fields
        |> field (TaskInputId.token (TaskInputId.Target InputTarget.OperationId)) get operationId

    let terminateOwnSessionsRequest =
        (record (fun operation settle retirement ->
            {
                OwnSessionTerminationRequest.OperationId = operation
                SettleOnly = settle
                ConfirmRetirement = retirement
            })
        : Fields<OwnSessionTerminationRequest, _>)
        |> terminationIdentity _.OperationId
        |> optionalField (TaskInputId.token TaskInputId.SettleOnly) _.SettleOnly boolean
        |> optionalField
            (TaskInputId.token TaskInputId.ConfirmRetirement)
            _.ConfirmRetirement
            boolean
        |> build
        |> named "OwnSessionTerminationRequest"

    let terminatePersonSessionsRequest =
        (record (fun actor operation settle retirement ->
            {
                PersonSessionTerminationRequest.ActorId = actor
                OperationId = operation
                SettleOnly = settle
                ConfirmRetirement = retirement
            })
        : Fields<PersonSessionTerminationRequest, _>)
        |> field (TaskInputId.token TaskInputId.ActorId) _.ActorId uuid
        |> terminationIdentity _.OperationId
        |> optionalField (TaskInputId.token TaskInputId.SettleOnly) _.SettleOnly boolean
        |> optionalField
            (TaskInputId.token TaskInputId.ConfirmRetirement)
            _.ConfirmRetirement
            boolean
        |> build
        |> named "PersonSessionTerminationRequest"

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

    let private dateFilter field =
        optionalField (CaseDateFilter.token field) (Map.tryFind field) CalendarDateCodecs.value

    let private dateFilters =
        record
            (fun
                registeredFrom
                registeredThrough
                receivedFrom
                receivedThrough
                closedFrom
                closedThrough
                inactiveSince ->
                [
                    CaseDateFilter.RegisteredFrom, registeredFrom
                    CaseDateFilter.RegisteredThrough, registeredThrough
                    CaseDateFilter.ReceivedFrom, receivedFrom
                    CaseDateFilter.ReceivedThrough, receivedThrough
                    CaseDateFilter.ClosedFrom, closedFrom
                    CaseDateFilter.ClosedThrough, closedThrough
                    CaseDateFilter.InactiveSince, inactiveSince
                ]
                |> List.choose (fun (key, value) -> value |> Option.map (fun value -> key, value))
                |> Map.ofList)
        |> dateFilter CaseDateFilter.RegisteredFrom
        |> dateFilter CaseDateFilter.RegisteredThrough
        |> dateFilter CaseDateFilter.ReceivedFrom
        |> dateFilter CaseDateFilter.ReceivedThrough
        |> dateFilter CaseDateFilter.ClosedFrom
        |> dateFilter CaseDateFilter.ClosedThrough
        |> dateFilter CaseDateFilter.InactiveSince

    let private filterFields =
        (record (fun (filter: CaseListFilter<string>) assignment due view responsibleActor dates ->
            { filter with
                Assignment = assignment
                Due = due
                View = view
                ResponsibleActor = responsibleActor
                Dates = dates
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
        |> flatten _.Dates dateFilters

    let caseListRequest =
        let paging =
            (record (fun cursor limit ->
                {
                    PageRequest.AfterCursor = cursor
                    Limit = limit
                })
            : Fields<PageRequest, _>)
            |> pageWith Paging.MaximumCaseCursorCharacters _.AfterCursor _.Limit

        let fields =
            (record (fun page filter ->
                {
                    CaseListRequest.Page = page
                    Filter = filter
                })
            : Fields<CaseListRequest, _>)
            |> flatten _.Page paging
            |> flatten _.Filter filterFields

        fields |> finishRead RequestSchemas.CaseListRequest

    let caseFilter = filterFields |> finishRead "CaseListSelection"

    let private caseSummaryFields =
        (record (fun filter groupBy ->
            {
                CaseSummarySelection.Filter = filter
                GroupBy = groupBy
            })
        : Fields<CaseSummarySelection, _>)
        |> flatten _.Filter filterFields
        |> optionalField "groupBy" _.GroupBy (VocabularyCodec.create CaseGrouping.vocabulary)

    let caseSummarySelection = caseSummaryFields |> finishRead "CaseSummarySelection"

    let caseSummaryRequest =
        let paging =
            (record (fun after limit -> { AfterCursor = after; Limit = limit })
            : Fields<PageRequest, _>)
            |> pageWith Paging.MaximumCaseCursorCharacters _.AfterCursor _.Limit

        (record (fun selection page ->
            {
                CaseSummaryRequest.Selection = selection
                Page = page
            })
        : Fields<CaseSummaryRequest, _>)
        |> flatten _.Selection caseSummaryFields
        |> flatten _.Page paging
        |> finishRead RequestSchemas.CaseSummaryRequest

    /// Shared selection fields omit traversal state; export and page requests consume this owner.
    let timelineSelectionFields =
        (record (fun target filter ->
            {
                CaseTimelineRequest.Target = target
                Filter = filter
                AfterCursor = None
                Limit = Paging.DefaultPageSize
            })
        : Fields<CaseTimelineRequest, _>)
        |> caseOperand _.Target
        |> optionalField "filter" _.Filter (VocabularyCodec.create TimelineFilter.vocabulary)

    let timelineSelection = timelineSelectionFields |> build

    let timelineRequest =
        (record (fun (selection: CaseTimelineRequest) cursor limit ->
            { selection with
                AfterCursor = cursor
                Limit = defaultArg limit Paging.DefaultPageSize
            })
        : Fields<CaseTimelineRequest, _>)
        |> flatten id timelineSelectionFields
        |> optionalField "cursor" _.AfterCursor (boundedText 1 Paging.MaximumActorCursorCharacters)
        |> optionalField
            "limit"
            (fun request -> Some request.Limit)
            (integer 1L (int64 Paging.MaximumPageSize) |> map int int64)
        |> finishRead "CaseTimelineRequest"

    let operationGetRequest =
        (record (fun operation ->
            {
                OperationLookup.OperationId = operation
            })
        : Fields<OperationLookup, _>)
        |> operationOperand _.OperationId
        |> finishRead RequestSchemas.OperationGetRequest

    let grantsRequest =
        (record (fun actor cursor limit ->
            {
                GrantsRequest.ActorId = actor
                AfterCursor = cursor
                Limit = limit
            })
        : Fields<GrantsRequest, _>)
        |> actorOperand _.ActorId
        |> page _.AfterCursor _.Limit
        |> finishRead RequestSchemas.GrantsRequest

    let empty = record () |> finishRead RequestSchemas.Empty

    let peopleHistorySelectionFields =
        (record (fun actor ->
            {
                PeopleHistoryRequest.TargetActorId = actor
                AfterCursor = None
                Limit = Paging.DefaultPageSize
            })
        : Fields<PeopleHistoryRequest, _>)
        |> actorFilter _.TargetActorId

    let peopleHistorySelection = peopleHistorySelectionFields |> build

    let peopleHistoryRequest =
        (record (fun (selection: PeopleHistoryRequest) cursor limit ->
            { selection with
                AfterCursor = cursor
                Limit = limit
            })
        : Fields<PeopleHistoryRequest, _>)
        |> flatten id peopleHistorySelectionFields
        |> page _.AfterCursor _.Limit
        |> finishRead RequestSchemas.PeopleHistoryRequest

    let reminderSelectionFields =
        (record (fun target state kind deadlineOutcome view due assignment search inactive ->
            {
                ReminderListRequest.Target = target
                View = view
                State = state
                Kind = kind
                DeadlineOutcome = deadlineOutcome
                Due = due
                Assignment = assignment
                Text = search
                AfterCursor = None
                Limit = Paging.DefaultPageSize
                IncludeInactive = inactive
            })
        : Fields<ReminderListRequest, _>)
        |> optionalField "target" _.Target caseSelector
        |> optionalField "state" _.State (VocabularyCodec.create ReminderStateSelection.vocabulary)
        |> optionalField "kind" _.Kind (VocabularyCodec.create ReminderKindSelection.vocabulary)
        |> optionalField
            "deadlineOutcome"
            _.DeadlineOutcome
            (VocabularyCodec.create DeadlineOutcome.vocabulary)
        |> optionalField "view" _.View (VocabularyCodec.create ReminderView.vocabulary)
        |> optionalField "due" _.Due (VocabularyCodec.create ReminderDue.vocabulary)
        |> optionalField
            "assignment"
            _.Assignment
            (VocabularyCodec.create CaseAssignment.vocabulary)
        |> optionalField "text" _.Text text
        |> optionalField "includeInactive" _.IncludeInactive boolean

    let reminderSelection = reminderSelectionFields |> build

    let reminderListRequest =
        (record (fun (selection: ReminderListRequest) cursor limit ->
            { selection with
                AfterCursor = cursor
                Limit = limit
            })
        : Fields<ReminderListRequest, _>)
        |> flatten id reminderSelectionFields
        |> optionalField "cursor" _.AfterCursor (boundedText 1 Paging.MaximumCaseCursorCharacters)
        |> field "limit" _.Limit (integer 1L (int64 Paging.MaximumPageSize) |> map int int64)
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
