namespace ClaimCore.Wire

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Presentation

module ScreenCodecs =
    open Codec
    open ResponseScalars
    open PresentationKeys
    open PresentedValueCodecs
    open PresentedMessageCodecs
    open LinkCodecs
    open AffordanceCodecs

    let private entry =
        (record (fun key label prominence value recorded ->
            {
                Entry.Key = key
                Recorded = recorded
                LabelId = label
                Prominence = prominence
                Value = value
            })
        : Fields<Entry, _>)
        |> field "key" _.Key key
        |> field "labelId" _.LabelId messageId
        |> field "prominence" _.Prominence (VocabularyCodec.create Prominence.vocabulary)
        |> field "value" _.Value value
        |> optionalField "recorded" _.Recorded boolean
        |> build
        |> named "PresentedEntry"

    let private section =
        (record (fun title entries emptyId ->
            {
                Section.TitleId = title
                Entries = entries
                EmptyMessageId = emptyId
            })
        : Fields<Section, _>)
        |> field "titleId" _.TitleId messageId
        |> field "entries" _.Entries (list MaximumEntries entry)
        |> optionalField "emptyId" _.EmptyMessageId messageId
        |> build
        |> named "PresentedSection"

    let private column =
        (record (fun key label prominence money recorded joins ->
            {
                Column.Key = key
                Recorded = defaultArg recorded false
                LabelId = label
                Prominence = prominence
                IsMoney = money
                Joins = joins
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
        |> field "joins" _.Joins (nullable key)
        |> build
        |> named "PresentedColumn"

    let private moneyMeter =
        (record (fun position maximum description ->
            {
                PresentedMoneyMeter.Position = position
                Maximum = maximum
                Description = description
            })
        : Fields<PresentedMoneyMeter, _>)
        |> field
            "position"
            _.Position
            (integer 0L (int64 PaymentsScreen.ProgressMaximum) |> map int int64)
        |> field
            "maximum"
            _.Maximum
            (integer (int64 PaymentsScreen.ProgressMaximum) (int64 PaymentsScreen.ProgressMaximum)
             |> map int int64)
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
        (record (fun stage reached date stageValue link actions ->
            {
                RegionStage.Stage = stage
                Link = link
                Actions = defaultArg actions []
                Reached = reached
                Date = date
                StageValue = stageValue
            })
        : Fields<RegionStage, _>)
        |> field "stage" _.Stage (VocabularyCodec.create CaseStageKind.vocabulary)
        |> field "reached" _.Reached boolean
        |> field "date" _.Date (nullable calendarDate)
        |> field "value" _.StageValue (nullable value)
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

    let private attachmentTransfer =
        (record (fun kind label request name ->
            {
                Kind = kind
                LabelId = label
                Request = request
                Name = name
            })
        : Fields<AttachmentTransferControl, _>)
        |> field "kind" _.Kind (VocabularyCodec.create AttachmentRead.vocabulary)
        |> field "labelId" _.LabelId key
        |> field "request" _.Request AttachmentCodecs.request
        |> field "name" _.Name AttachmentCodecs.name
        |> build
        |> named "AttachmentTransferControl"

    let private rowChoice =
        (record (fun value label ->
            {
                ChoiceValue = value
                ChoiceLabel = label
            })
        : Fields<RegionChoice, _>)
        |> field "value" _.ChoiceValue key
        |> field "label" _.ChoiceLabel message
        |> build
        |> named "RegionChoice"

    let private row =
        (record (fun key values choice detail link actions transfers recommended prominence ->
            {
                RegionRow.Key = key
                Values = values
                Choice = choice
                Detail = detail
                Link = link
                Actions = defaultArg actions []
                Transfers = defaultArg transfers []
                Recommended = recommended
                Prominence = prominence
            })
        : Fields<RegionRow, _>)
        |> field "key" _.Key key
        |> field "values" _.Values (list MaximumEntries value)
        |> optionalField "choice" _.Choice rowChoice
        |> optionalField "detail" _.Detail rowDetail
        |> optionalField "link" _.Link link
        |> optionalField
            "actions"
            (fun value -> if value.Actions.IsEmpty then None else Some value.Actions)
            actions
        |> optionalField
            "transfers"
            (fun value ->
                if value.Transfers.IsEmpty then
                    None
                else
                    Some value.Transfers)
            (list AttachmentRead.all.Length attachmentTransfer)
        |> optionalField
            "recommended"
            _.Recommended
            (VocabularyCodec.create TaskVocabulary.vocabulary)
        |> optionalField "prominence" _.Prominence (VocabularyCodec.create Prominence.vocabulary)
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
        (record
            (fun key whenAt who target changes changedFields summary highlights details link actions ->
                {
                    RegionTimelineEntry.Key = key
                    Link = link
                    Actions = defaultArg actions []
                    When = whenAt
                    Who = who
                    Target = target
                    Changes = defaultArg changes []
                    ChangedFields = defaultArg changedFields []
                    Summary = summary
                    Highlights = highlights
                    Details = details
                })
        : Fields<RegionTimelineEntry, _>)
        |> field "key" _.Key key
        |> field "when" _.When instant
        |> field "who" _.Who value
        |> optionalField "target" _.Target value
        |> optionalField
            "changes"
            (fun entry -> if entry.Changes.IsEmpty then None else Some entry.Changes)
            (list MaximumEntries change)
        |> optionalField
            "changedFields"
            (fun entry ->
                if entry.ChangedFields.IsEmpty then
                    None
                else
                    Some entry.ChangedFields)
            (list MaximumEntries messageId)
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
                    (record id
                     |> field "notices" id (boundedList 1 RegionNotice.maximum (build noticeFields)))
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

    let private regionCount =
        let subjects = ListCounts.sectionMessages |> Map.keys |> Seq.toList

        (record (fun subject value ->
            {
                RegionCount.CountKind = subject
                Quantity = value
            })
        : Fields<RegionCount, _>)
        |> field
            "kind"
            _.CountKind
            (Vocabulary.define "sectionCount" subjects id |> VocabularyCodec.create)
        |> field "value" _.Quantity (integer 0L SafeInteger.Maximum)
        |> build
        |> named "RegionCount"

    let private region =
        (record (fun role title emptyId count content link actions ->
            {
                Region.Role = role
                Link = link
                Actions = defaultArg actions []
                TitleId = title
                EmptyId = emptyId
                Count = count
                Content = content
            })
        : Fields<Region, _>)
        |> field "role" _.Role (VocabularyCodec.create RegionRole.vocabulary)
        |> optionalField "titleId" _.TitleId messageId
        |> optionalField "emptyId" _.EmptyId messageId
        |> optionalField "count" _.Count regionCount
        |> field "content" _.Content content
        |> optionalField "link" _.Link link
        |> optionalField
            "actions"
            (fun value -> if value.Actions.IsEmpty then None else Some value.Actions)
            actions
        |> build
        |> named "Region"

    let screen =
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
            Screen.create regions |> Screen.withCurrentGroups (defaultArg groups []))
        : Fields<Screen, _>)
        |> field
            "regions"
            _.Regions
            (let regions = list MaximumEntries region

             restrict
                 (Schema.KeyedArray(regions.Schema, "role", PresentationStructure.singletonRegions))
                 PresentationStructure.regions
                 regions)
        |> optionalField "currentGroups" (_.CurrentGroups >> supplied) (list 2 currentGroup)
        |> build
        |> fun codec -> restrict codec.Schema PresentationStructure.screen codec
        |> named "Screen"
