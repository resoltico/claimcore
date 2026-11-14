namespace ClaimCore.Wire

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Presentation

/// Presentation is trusted server data with the same structural admission in every generated consumer.
module PresentationCodecs =
    open Codec
    open ResponseScalars

    let MaximumEntries =
        max
            (CaseFields.declarations.Length + 2)
            (2 * PeopleAdmission.MaximumGrants * 3 + Capability.all.Length * 3 + 3)

    let MaximumKeyCharacters = 256

    let MaximumCodes =
        max ActKind.all.Length (max Role.all.Length Capability.all.Length)

    let private textMaximum =
        max IncidentDescription.MaximumBytes HandlingSummary.MaximumBytes

    let private textValue = boundedText 0 textMaximum
    let private identifier = boundedText 1 textMaximum
    let private key = boundedText 1 MaximumKeyCharacters

    let private messageId =
        let pattern = "^[A-Za-z][A-Za-z0-9_.]*(?![\\s\\S])"

        patternedText 1 MaximumKeyCharacters pattern None

    let private displayScale =
        integer 0L (int64 Money.MaximumFractionalDigits) |> map int int64

    let private parsed parse format codec =
        codec
        |> interpret
            (fun path raw ->
                parse raw
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Value outside its presentation grammar."
                    }))
            format

    let private amountValue = parsed Amount.parse Amount.format amount
    let private currencyValue = parsed Currency.parse Currency.format currency

    let private totalValue =
        let maximum =
            DecimalTotal.MaximumIntegerDigits + DecimalTotal.MaximumFractionalDigits + 1

        let pattern = "^(?:" + DecimalTotal.Grammar + ")(?![\\s\\S])"

        patternedText 1 maximum pattern None
        |> parsed DecimalTotal.parse DecimalTotal.format

    let private moneyFields =
        (record (fun amount currency scale ->
            {
                PresentedMoney.Value = Money.create amount currency
                DisplayScale = scale
            })
        : Fields<PresentedMoney, _>)
        |> field "amount" (fun value -> Money.amount value.Value) amountValue
        |> field "currency" (fun value -> Money.currencyValue value.Value) currencyValue
        |> field "displayScale" _.DisplayScale displayScale

    let private totalFields =
        (record (fun amount currency scale ->
            {
                PresentedMoneyTotal.Currency = currency
                Value = amount
                DisplayScale = scale
            })
        : Fields<PresentedMoneyTotal, _>)
        |> field "amount" _.Value totalValue
        |> field "currency" _.Currency currencyValue
        |> field "displayScale" _.DisplayScale displayScale

    let private tokens vocabulary =
        VocabularyCodec.create vocabulary
        |> map (Vocabulary.token vocabulary) (fun token ->
            Vocabulary.tryParse vocabulary token
            |> Option.defaultWith (fun () ->
                invalidOp "A presented code must belong to its declared vocabulary."))

    let private codeVariant many vocabulary =
        let name = Vocabulary.name vocabulary

        if many then
            variant
                name
                (fun items -> PresentedValue.Codes(name, items))
                (function
                | PresentedValue.Codes(vocabulary, items) when vocabulary = name -> Some items
                | _ -> None)
                (record id
                 |> fixedField "kind" "CODES"
                 |> field "tokens" id (list MaximumCodes (tokens vocabulary)))
        else
            variant
                name
                (fun token -> PresentedValue.Code(name, token))
                (function
                | PresentedValue.Code(vocabulary, token) when vocabulary = name -> Some token
                | _ -> None)
                (record id |> fixedField "kind" "CODE" |> field "token" id (tokens vocabulary))

    let private codes many =
        [
            codeVariant many Lifecycle.vocabulary
            codeVariant many CaseStatus.vocabulary
            codeVariant many CaseProgress.vocabulary
            codeVariant many CaseAssignment.vocabulary
            codeVariant many Role.vocabulary
            codeVariant many ScopeKind.vocabulary
            codeVariant many Capability.vocabulary
            codeVariant many ReminderState.vocabulary
            codeVariant many PresentedBoolean.vocabulary
            codeVariant many CorrectionGroupAction.vocabulary
            codeVariant many ActKind.vocabulary
            codeVariant many CaseStageKind.vocabulary
            codeVariant many CaseAttention.vocabulary
            codeVariant many AccountState.vocabulary
            codeVariant many PrincipalKind.vocabulary
            codeVariant many RequesterKind.vocabulary
            codeVariant many ClaimCore.Application.Contracts.View.vocabulary
            codeVariant many CaseDue.vocabulary
            codeVariant many ReminderDue.vocabulary
        ]
        |> taggedOn "vocabulary" (if many then "PresentedCodes" else "PresentedCode")

    // This complete wire-tag table keeps every semantic constructor beside its closed payload grammar.
    let private scalar =
        tagged
            "PresentedScalar"
            [
                variant
                    "MONEY"
                    PresentedValue.Money
                    (function
                    | PresentedValue.Money value -> Some value
                    | _ -> None)
                    moneyFields
                variant
                    "MONEY_TOTAL"
                    PresentedValue.MoneyTotal
                    (function
                    | PresentedValue.MoneyTotal value -> Some value
                    | _ -> None)
                    totalFields
                variant
                    "DATE"
                    PresentedValue.Date
                    (function
                    | PresentedValue.Date value -> Some value
                    | _ -> None)
                    (record id |> field "value" id calendarDate)
                variant
                    "INSTANT"
                    PresentedValue.Instant
                    (function
                    | PresentedValue.Instant value -> Some value
                    | _ -> None)
                    (record id |> field "value" id instant)
                variant
                    "PARTY"
                    PresentedValue.Party
                    (function
                    | PresentedValue.Party value -> Some value
                    | _ -> None)
                    (record id |> field "value" id partyName)
                variant
                    "TEXT"
                    PresentedValue.Text
                    (function
                    | PresentedValue.Text value -> Some value
                    | _ -> None)
                    (record id |> field "value" id textValue)
                variant
                    "NARRATIVE"
                    PresentedValue.Narrative
                    (function
                    | PresentedValue.Narrative value -> Some value
                    | _ -> None)
                    (record id |> field "value" id textValue)
                variant
                    "MESSAGE"
                    PresentedValue.Message
                    (function
                    | PresentedValue.Message value -> Some value
                    | _ -> None)
                    (record id |> field "messageId" id messageId)
                variant
                    "ACTOR"
                    PresentedValue.Actor
                    (function
                    | PresentedValue.Actor value -> Some value
                    | _ -> None)
                    (record id |> field "value" id ActorCodecs.actorRef)
                variant
                    "STATE"
                    PresentedValue.State
                    (function
                    | PresentedValue.State value -> Some value
                    | _ -> None)
                    (record id
                     |> fixedField "vocabulary" (Vocabulary.name CaseHeadline.vocabulary)
                     |> field "token" id (VocabularyCodec.create CaseHeadline.vocabulary))
                variant
                    "COUNT"
                    PresentedValue.Count
                    (function
                    | PresentedValue.Count value -> Some value
                    | _ -> None)
                    (record id |> field "value" id (integer 0L SafeInteger.Maximum))
                variant
                    "REFERENCE"
                    PresentedValue.Reference
                    (function
                    | PresentedValue.Reference value -> Some value
                    | _ -> None)
                    (record id |> field "value" id caseReference)
                variant
                    "IDENTIFIER"
                    PresentedValue.Identifier
                    (function
                    | PresentedValue.Identifier value -> Some value
                    | _ -> None)
                    (record id |> field "value" id identifier)
                variant
                    "ABSENT"
                    PresentedValue.Absent
                    (function
                    | PresentedValue.Absent value -> Some value
                    | _ -> None)
                    (record id |> field "reasonId" id messageId)
                variant
                    "COUNTRY"
                    (CountryCode.format >> fun token -> PresentedValue.Code("country", token))
                    (function
                    | PresentedValue.Code("country", token) ->
                        CountryCode.parse token |> Result.toOption
                    | _ -> None)
                    (record id |> field "value" id countryCode)
                variant
                    "CURRENCY"
                    (Currency.format >> fun token -> PresentedValue.Code("currency", token))
                    (function
                    | PresentedValue.Code("currency", token) ->
                        Currency.parse token |> Result.toOption
                    | _ -> None)
                    (record id |> field "value" id currencyValue)
            ]

    let value =
        choice
            "PresentedValue"
            [
                branch
                    id
                    (function
                    | (PresentedValue.Code("country", _) | PresentedValue.Code("currency", _)) as value ->
                        Some value
                    | PresentedValue.Code _
                    | PresentedValue.Codes _ -> None
                    | value -> Some value)
                    scalar
                branch
                    id
                    (function
                    | PresentedValue.Code(vocabulary, _) as value when
                        vocabulary <> "country" && vocabulary <> "currency"
                        ->
                        Some value
                    | _ -> None)
                    (codes false)
                branch
                    id
                    (function
                    | PresentedValue.Codes _ as value -> Some value
                    | _ -> None)
                    (codes true)
            ]

    let private change =
        (record (fun key label before after ->
            {
                PresentedChange.Key = key
                LabelId = label
                Before = before
                After = after
            })
        : Fields<PresentedChange, _>)
        |> field "key" _.Key key
        |> field "labelId" _.LabelId messageId
        |> field "before" _.Before value
        |> field "after" _.After value
        |> build
        |> named "PresentedChange"

    let private unchanged =
        (record (fun key label ->
            {
                PresentedField.Key = key
                LabelId = label
            })
        : Fields<PresentedField, _>)
        |> field "key" _.Key key
        |> field "labelId" _.LabelId messageId
        |> build
        |> named "PresentedField"

    let review =
        (record (fun changes unchanged ->
            {
                ReviewPresentation.Changes = changes
                Unchanged = unchanged
            })
        : Fields<ReviewPresentation, _>)
        |> field "changes" _.Changes (list MaximumEntries change)
        |> field "unchanged" _.Unchanged (list MaximumEntries unchanged)
        |> build
        |> named "ReviewPresentation"

    let private messageArguments =
        record (fun key value -> key, value)
        |> field "key" fst key
        |> field "value" snd value
        |> build
        |> named "PresentedArgument"
        |> list MaximumEntries
        |> fun codec -> restrict codec.Schema PresentationStructure.arguments codec
        |> map Map.ofList Map.toList

    let message =
        (record (fun message arguments ->
            {
                PresentedMessage.MessageId = message
                Arguments = arguments
            })
        : Fields<PresentedMessage, _>)
        |> field "messageId" _.MessageId messageId
        |> field "arguments" _.Arguments messageArguments
        |> build
        |> named "PresentedMessage"

    let advice =
        record (fun message action ->
            {
                HumanAdvice.Message = message
                Action = action
            })
        |> field "message" _.Message message
        |> field "action" _.Action (VocabularyCodec.create RecommendedAction.vocabulary)
        |> build
        |> named "HumanAdvice"

    let link =
        let query task inject extract codec =
            variant (QueryTaskId.key task) inject extract (record id |> field "input" id codec)

        taggedOn
            "endpoint"
            "Link"
            [
                query
                    QueryTaskId.Context
                    (fun () -> Link.Context)
                    (function
                    | Link.Context -> Some()
                    | _ -> None)
                    RequestCodecs.empty
                query
                    QueryTaskId.CaseGet
                    Link.CaseGet
                    (function
                    | Link.CaseGet value -> Some value
                    | _ -> None)
                    RequestCodecs.caseGetRequest
                query
                    QueryTaskId.CaseAssignees
                    Link.CaseAssignees
                    (function
                    | Link.CaseAssignees value -> Some value
                    | _ -> None)
                    RequestCodecs.assigneeRequest
                query
                    QueryTaskId.CaseList
                    Link.CaseList
                    (function
                    | Link.CaseList value -> Some value
                    | _ -> None)
                    RequestCodecs.caseListRequest
                query
                    QueryTaskId.CaseSummary
                    Link.CaseSummary
                    (function
                    | Link.CaseSummary value -> Some value
                    | _ -> None)
                    RequestCodecs.caseSummaryRequest
                query
                    QueryTaskId.CaseTimeline
                    Link.CaseTimeline
                    (function
                    | Link.CaseTimeline value -> Some value
                    | _ -> None)
                    RequestCodecs.timelineRequest
                query
                    QueryTaskId.CaseNotes
                    Link.CaseNotes
                    (function
                    | Link.CaseNotes value -> Some value
                    | _ -> None)
                    RequestCodecs.caseNotesRequest
                query
                    QueryTaskId.CasePayments
                    Link.CasePayments
                    (function
                    | Link.CasePayments value -> Some value
                    | _ -> None)
                    RequestCodecs.casePaymentsRequest
                query
                    QueryTaskId.ReminderList
                    Link.ReminderList
                    (function
                    | Link.ReminderList value -> Some value
                    | _ -> None)
                    RequestCodecs.reminderListRequest
                query
                    QueryTaskId.ReminderGet
                    Link.ReminderGet
                    (function
                    | Link.ReminderGet value -> Some value
                    | _ -> None)
                    RequestCodecs.reminderGetRequest
                query
                    QueryTaskId.WorkViews
                    (fun () -> Link.WorkViews)
                    (function
                    | Link.WorkViews -> Some()
                    | _ -> None)
                    RequestCodecs.empty
                query
                    QueryTaskId.OperationGet
                    (function
                    | Some id -> Link.OperationGet { OperationId = id }
                    | None -> Link.OperationLookupForm)
                    (function
                    | Link.OperationGet value -> Some(Some value.OperationId)
                    | Link.OperationLookupForm -> Some None
                    | _ -> None)
                    (record id |> optionalField "operationId" id uuid |> build)
                query
                    QueryTaskId.PeopleList
                    Link.PeopleList
                    (function
                    | Link.PeopleList value -> Some value
                    | _ -> None)
                    RequestCodecs.actorListRequest
                query
                    QueryTaskId.PeopleGrants
                    Link.PeopleGrants
                    (function
                    | Link.PeopleGrants value -> Some value
                    | _ -> None)
                    RequestCodecs.grantsRequest
                query
                    QueryTaskId.PeopleHistory
                    Link.PeopleHistory
                    (function
                    | Link.PeopleHistory value -> Some value
                    | _ -> None)
                    RequestCodecs.peopleHistoryRequest
                query
                    QueryTaskId.PeopleRequests
                    Link.PeopleRequests
                    (function
                    | Link.PeopleRequests value -> Some value
                    | _ -> None)
                    RequestCodecs.accessRequestsRequest
            ]

    let namedLink =
        record (fun relation label link ->
            {
                NamedLink.Relation = relation
                LabelId = label
                Link = link
            })
        |> field "rel" _.Relation (VocabularyCodec.create LinkRelation.vocabulary)
        |> field "labelId" _.LabelId messageId
        |> field "link" _.Link link
        |> build
        |> named "NamedLink"

    let affordance =
        let variants =
            TaskDeclarations.all
            |> List.choose (fun declaration ->
                let binding =
                    match declaration.Task with
                    | TaskId.Operation task -> Some(OperationSubmissionCodecs.binding task)
                    | TaskId.Command CommandTaskId.PeopleRequestsDismiss ->
                        Some(
                            record (OperationBinding.People None)
                            |> fixedField "kind" "PEOPLE"
                            |> build
                        )
                    | TaskId.Query _ -> None

                binding
                |> Option.map (fun binding ->
                    let allowed = declaration.Inputs |> List.map _.Input

                    let identifiers =
                        allowed
                        @ (declaration.Collections
                           |> List.map (fun collection ->
                               TaskCollectionId.input collection.Collection))

                    let input =
                        Vocabulary.define
                            TaskInputId.vocabularyName
                            TaskInputId.all
                            TaskInputId.token
                        |> VocabularyCodec.select (fun input -> List.contains input identifiers)

                    let choices =
                        allowed
                        |> List.choose (fun input ->
                            TaskChoiceId.ofInput input
                            |> Option.map (fun choice ->
                                let tokens = TaskChoiceId.tokens choice

                                input,
                                TaskInputId.token input,
                                true,
                                list
                                    tokens.Length
                                    (Vocabulary.define
                                        (Vocabulary.token TaskChoiceId.vocabulary choice)
                                        tokens
                                        id
                                     |> VocabularyCodec.create)))
                        |> fieldMapWithPresence

                    variant
                        (TaskId.key declaration.Task)
                        id
                        (fun value -> if value.Task = declaration.Task then Some value else None)
                        (record (fun binding seeds editable choices primary blocked ->
                            {
                                Affordance.Task = declaration.Task
                                Binding = binding
                                Seeds = seeds
                                Editable = editable
                                Choices = choices
                                Primary = primary
                                Blocked = blocked
                            })
                         |> field "binding" _.Binding binding
                         |> field
                             "seeds"
                             _.Seeds
                             (OperationSubmissionCodecs.seedInputs declaration)
                         |> field "editable" _.Editable (list identifiers.Length input)
                         |> field "choices" _.Choices choices
                         |> field "primary" _.Primary boolean
                         |> field "blocked" _.Blocked (nullable advice))))

        taggedOn "task" "Affordance" variants

    let private actions = list TaskId.all.Length affordance

    let entry =
        (record (fun key label prominence value recorded ->
            {
                Entry.Key = key
                Recorded = defaultArg recorded false
                LabelId = label
                Prominence = prominence
                Value = value
            })
        : Fields<Entry, _>)
        |> field "key" _.Key key
        |> field "labelId" _.LabelId messageId
        |> field "prominence" _.Prominence (VocabularyCodec.create Prominence.vocabulary)
        |> field "value" _.Value value
        |> optionalField
            "recorded"
            (fun value -> if value.Recorded then Some true else None)
            boolean
        |> build
        |> named "PresentedEntry"

    let private section =
        (record (fun title entries ->
            {
                Section.TitleId = title
                Entries = entries
            })
        : Fields<Section, _>)
        |> field "titleId" _.TitleId messageId
        |> field "entries" _.Entries (list MaximumEntries entry)
        |> build
        |> named "PresentedSection"

    let private column =
        (record (fun key label prominence money recorded ->
            {
                Column.Key = key
                Recorded = defaultArg recorded false
                LabelId = label
                Prominence = prominence
                IsMoney = money
            })
        : Fields<Column, _>)
        |> field "key" _.Key key
        |> field "labelId" _.LabelId messageId
        |> field "prominence" _.Prominence (VocabularyCodec.create Prominence.vocabulary)
        |> field "isMoney" _.IsMoney boolean
        |> optionalField
            "recorded"
            (fun value -> if value.Recorded then Some true else None)
            boolean
        |> build
        |> named "PresentedColumn"

    let private moneyMeter =
        (record (fun current target complete description ->
            {
                PresentedMoneyMeter.Current = current
                Target = target
                Complete = complete
                Description = description
            })
        : Fields<PresentedMoneyMeter, _>)
        |> field "current" _.Current (moneyFields |> build)
        |> field "target" _.Target (moneyFields |> build)
        |> field "complete" _.Complete boolean
        |> field "description" _.Description message
        |> build
        |> named "PresentedMoneyMeter"

    let private rowDetail =
        (record (fun summary highlights sections ->
            {
                RowDetail.Summary = summary
                Highlights = highlights
                Sections = sections
            })
        : Fields<RowDetail, _>)
        |> field "summary" _.Summary (nullable message)
        |> field "highlights" _.Highlights (list MaximumEntries section)
        |> field "sections" _.Sections (list MaximumEntries section)
        |> build
        |> named "PresentedRowDetail"

    let private stage =
        (record (fun stage reached date amount meter link actions ->
            {
                RegionStage.Stage = stage
                Link = link
                Actions = defaultArg actions []
                Meter = meter
                Reached = reached
                Date = date
                Amount = amount
            })
        : Fields<RegionStage, _>)
        |> field "stage" _.Stage (VocabularyCodec.create CaseStageKind.vocabulary)
        |> field "reached" _.Reached boolean
        |> field "date" _.Date (nullable calendarDate)
        |> field "amount" _.Amount (nullable (moneyFields |> build))
        |> optionalField "meter" _.Meter moneyMeter
        |> optionalField "link" _.Link link
        |> optionalField
            "actions"
            (fun value -> if value.Actions.IsEmpty then None else Some value.Actions)
            actions
        |> build
        |> named "RegionStage"

    let private stagesFields =
        (record (fun current steps ->
            {
                RegionStages.Current = current
                Steps = steps
            })
        : Fields<RegionStages, _>)
        |> field "current" _.Current (VocabularyCodec.create CaseStageKind.vocabulary)
        |> field "steps" _.Steps (list CaseStageKind.all.Length stage)

    let private row =
        (record (fun key values detail link actions ->
            {
                RegionRow.Key = key
                Values = values
                Detail = detail
                Link = link
                Actions = defaultArg actions []
            })
        : Fields<RegionRow, _>)
        |> field "key" _.Key key
        |> field "values" _.Values (list MaximumEntries value)
        |> optionalField "detail" _.Detail rowDetail
        |> optionalField "link" _.Link link
        |> optionalField
            "actions"
            (fun value -> if value.Actions.IsEmpty then None else Some value.Actions)
            actions
        |> build
        |> named "RegionRow"

    let private rowGroup =
        (record (fun title count keys ->
            {
                RegionRowGroup.TitleId = title
                Count = count
                RowKeys = keys
            })
        : Fields<RegionRowGroup, _>)
        |> field "titleId" _.TitleId messageId
        |> field "count" _.Count (integer 0L SafeInteger.Maximum)
        |> field "rowKeys" _.RowKeys (list 50 key)
        |> build
        |> named "RegionRowGroup"

    let private supplied =
        function
        | [] -> None
        | values -> Some values

    let private tableFields =
        (record (fun columns rows groups sections meter ->
            {
                RegionTable.Columns = columns
                Meter = meter
                Rows = rows
                Groups = defaultArg groups []
                Sections = defaultArg sections []
            })
        : Fields<RegionTable, _>)
        |> field "columns" _.Columns (list MaximumEntries column)
        |> field "rows" _.Rows (list 50 row)
        |> optionalField "groups" (_.Groups >> supplied) (list MaximumEntries rowGroup)
        |> optionalField "sections" (_.Sections >> supplied) (list MaximumEntries section)
        |> optionalField "meter" _.Meter moneyMeter

    let private noticeFields =
        (record (fun message reason tone ->
            {
                RegionNotice.MessageId = message
                Reason = reason
                Tone = tone
            })
        : Fields<RegionNotice, _>)
        |> field "messageId" _.MessageId messageId
        |> optionalField "reason" _.Reason value
        |> field "tone" _.Tone (VocabularyCodec.create NoticeTone.vocabulary)

    let private timelineEntry =
        (record (fun key whenAt who summary highlights details link actions ->
            {
                RegionTimelineEntry.Key = key
                Link = link
                Actions = defaultArg actions []
                When = whenAt
                Who = who
                Summary = summary
                Highlights = highlights
                Details = details
            })
        : Fields<RegionTimelineEntry, _>)
        |> field "key" _.Key key
        |> field "when" _.When instant
        |> field "who" _.Who ActorCodecs.actorRef
        |> field "summary" _.Summary message
        |> field "highlights" _.Highlights (list MaximumEntries section)
        |> field "details" _.Details (list MaximumEntries section)
        |> optionalField "link" _.Link link
        |> optionalField
            "actions"
            (fun value -> if value.Actions.IsEmpty then None else Some value.Actions)
            actions
        |> build
        |> named "RegionTimelineEntry"

    let private ordinaryContent =
        let tag = RegionContentKind.token

        tagged
            "RegionContent.Body"
            [
                variant
                    (tag RegionContentKind.Entries)
                    RegionContent.Entries
                    (function
                    | RegionContent.Entries entries -> Some entries
                    | _ -> None)
                    (record id |> field "entries" id (list MaximumEntries entry))
                variant
                    (tag RegionContentKind.Table)
                    RegionContent.Table
                    (function
                    | RegionContent.Table table -> Some table
                    | _ -> None)
                    tableFields
                variant
                    (tag RegionContentKind.Stages)
                    RegionContent.Stages
                    (function
                    | RegionContent.Stages stages -> Some stages
                    | _ -> None)
                    stagesFields
                variant
                    (tag RegionContentKind.Notice)
                    RegionContent.Notice
                    (function
                    | RegionContent.Notice notice -> Some notice
                    | _ -> None)
                    noticeFields
                variant
                    (tag RegionContentKind.Timeline)
                    RegionContent.Timeline
                    (function
                    | RegionContent.Timeline entries -> Some entries
                    | _ -> None)
                    (record id |> field "entries" id (list 50 timelineEntry))
            ]
        |> Codec.vocabulary (Vocabulary.name RegionContentKind.vocabulary)

    let private codeContent =
        codes true
        |> map
            (function
            | PresentedValue.Codes(vocabulary, tokens) -> RegionContent.Codes(vocabulary, tokens)
            | _ -> invalidOp "A code region contains codes.")
            (function
             | RegionContent.Codes(vocabulary, tokens) -> PresentedValue.Codes(vocabulary, tokens)
             | _ -> invalidArg "content" "A code region contains codes.")

    let private content =
        choice
            "RegionContent"
            [
                branch
                    id
                    (function
                    | RegionContent.Codes _ -> None
                    | content -> Some content)
                    ordinaryContent
                branch
                    id
                    (function
                    | RegionContent.Codes _ as content -> Some content
                    | _ -> None)
                    codeContent
            ]
        |> fun codec -> restrict codec.Schema PresentationStructure.content codec

    let private region =
        (record (fun role title content link actions ->
            {
                Region.Role = role
                Link = link
                Actions = defaultArg actions []
                TitleId = title
                Content = content
            })
        : Fields<Region, _>)
        |> field "role" _.Role (VocabularyCodec.create RegionRole.vocabulary)
        |> optionalField "titleId" _.TitleId messageId
        |> field "content" _.Content content
        |> optionalField "link" _.Link link
        |> optionalField
            "actions"
            (fun value -> if value.Actions.IsEmpty then None else Some value.Actions)
            actions
        |> build
        |> named "Region"

    let view =
        let currentGroup =
            (record (fun group section ->
                {
                    CurrentInputGroup.Group = group
                    Section = section
                })
            : Fields<CurrentInputGroup, _>)
            |> field
                "group"
                _.Group
                (VocabularyCodec.select ((<>) RecordGroup.Payment) RecordGroup.vocabulary)
            |> field "section" _.Section section
            |> build

        (record (fun regions groups ->
            {
                Screen.Regions = regions
                CurrentGroups = defaultArg groups []
            })
        : Fields<Screen, _>)
        |> field "regions" _.Regions (list MaximumEntries region)
        |> optionalField "currentGroups" (_.CurrentGroups >> supplied) (list 2 currentGroup)
        |> build
        |> fun codec -> restrict codec.Schema PresentationStructure.screen codec
        |> named "Screen"

    let presented codec =
        (record (fun data view -> { Presented.Data = data; View = view })
        : Fields<Presented<'data>, _>)
        |> field "data" _.Data codec
        |> field "view" _.View view
