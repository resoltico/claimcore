namespace ClaimCore.Wire

open System
open System.Text.RegularExpressions
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
            (2 * AuthorityAdmission.MaximumGrants * 3 + Capability.all.Length * 3 + 3)

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

        boundedText 1 MaximumKeyCharacters
        |> constrained
            (Schema.String(Some pattern, None, Some 1, Some MaximumKeyCharacters, None))
            (fun value -> Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant))

    let private displayScale =
        integer 0L (int64 Money.MaximumFractionalDigits) |> map int int64

    let private parsed parse format codec =
        codec
        |> mapResult
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

        boundedText 1 maximum
        |> constrained (Schema.String(Some pattern, None, Some 1, Some maximum, None)) (fun value ->
            Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant))
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
            codeVariant many WorkActKind.vocabulary
            codeVariant many ReminderActKind.vocabulary
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

    let private entry =
        (record (fun key label prominence value ->
            {
                Entry.Key = key
                LabelId = label
                Prominence = prominence
                Value = value
            })
        : Fields<Entry, _>)
        |> field "key" _.Key key
        |> field "labelId" _.LabelId messageId
        |> field "prominence" _.Prominence (VocabularyCodec.create Prominence.vocabulary)
        |> field "value" _.Value value
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
        (record (fun key label prominence money ->
            {
                Column.Key = key
                LabelId = label
                Prominence = prominence
                IsMoney = money
            })
        : Fields<Column, _>)
        |> field "key" _.Key key
        |> field "labelId" _.LabelId messageId
        |> field "prominence" _.Prominence (VocabularyCodec.create Prominence.vocabulary)
        |> field "isMoney" _.IsMoney boolean
        |> build
        |> named "PresentedColumn"

    let private messageArguments =
        record (fun key value -> key, value)
        |> field "key" fst key
        |> field "value" snd value
        |> build
        |> named "PresentedArgument"
        |> list MaximumEntries
        |> mapResult
            (fun path pairs ->
                let arguments = Map.ofList pairs

                if arguments.Count = pairs.Length then
                    Ok arguments
                else
                    Error
                        {
                            Path = path
                            Reason = "Message argument keys must be unique."
                        })
            Map.toList

    let private presentedMessage =
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

    let private rowDetail =
        (record (fun summary highlights sections ->
            {
                RowDetail.Summary = summary
                Highlights = highlights
                Sections = sections
            })
        : Fields<RowDetail, _>)
        |> field "summary" _.Summary (nullable presentedMessage)
        |> field "highlights" _.Highlights (list MaximumEntries section)
        |> field "sections" _.Sections (list MaximumEntries section)
        |> build
        |> named "PresentedRowDetail"

    let private stage =
        (record (fun stage reached date amount ->
            {
                RegionStage.Stage = stage
                Reached = reached
                Date = date
                Amount = amount
            })
        : Fields<RegionStage, _>)
        |> field "stage" _.Stage (VocabularyCodec.create CaseStageKind.vocabulary)
        |> field "reached" _.Reached boolean
        |> field "date" _.Date (nullable calendarDate)
        |> field "amount" _.Amount (nullable (moneyFields |> build))
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
        (record (fun key values detail ->
            {
                RegionRow.Key = key
                Values = values
                Detail = detail
            })
        : Fields<RegionRow, _>)
        |> field "key" _.Key key
        |> field "values" _.Values (list MaximumEntries value)
        |> optionalField "detail" _.Detail rowDetail
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
        (record (fun columns rows groups sections ->
            {
                RegionTable.Columns = columns
                Rows = rows
                Groups = defaultArg groups []
                Sections = defaultArg sections []
            })
        : Fields<RegionTable, _>)
        |> field "columns" _.Columns (list MaximumEntries column)
        |> field "rows" _.Rows (list 50 row)
        |> optionalField "groups" (_.Groups >> supplied) (list MaximumEntries rowGroup)
        |> optionalField "sections" (_.Sections >> supplied) (list MaximumEntries section)

    let private actionGroup =
        (record (fun family acts ->
            {
                RegionActionGroup.Family = family
                Acts = acts
            })
        : Fields<RegionActionGroup, _>)
        |> field "family" _.Family (VocabularyCodec.create ActFamily.vocabulary)
        |> field "acts" _.Acts (list MaximumCodes (VocabularyCodec.create ActKind.vocabulary))
        |> build
        |> named "RegionActionGroup"

    let private actionsFields =
        (record (fun recommended groups ->
            {
                RegionActions.Recommended = recommended
                Groups = groups
            })
        : Fields<RegionActions, _>)
        |> field "recommended" _.Recommended (nullable (VocabularyCodec.create ActKind.vocabulary))
        |> field
            "groups"
            _.Groups
            (list (Vocabulary.all ActFamily.vocabulary |> List.length) actionGroup)

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
        (record (fun key whenAt who summary highlights details ->
            {
                RegionTimelineEntry.Key = key
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
        |> field "summary" _.Summary presentedMessage
        |> field "highlights" _.Highlights (list MaximumEntries section)
        |> field "details" _.Details (list MaximumEntries section)
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
                    (tag RegionContentKind.Actions)
                    RegionContent.Actions
                    (function
                    | RegionContent.Actions actions -> Some actions
                    | _ -> None)
                    actionsFields
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

    let private validTable (table: RegionTable) =
        let keys = table.Rows |> List.map _.Key
        let grouped = table.Groups |> List.collect _.RowKeys

        table.Rows |> List.forall (fun row -> row.Values.Length = table.Columns.Length)
        && (List.distinct keys).Length = keys.Length
        && (List.distinct grouped).Length = grouped.Length
        && (table.Groups.IsEmpty || Set.ofList keys = Set.ofList grouped)
        && (table.Groups
            |> List.forall (fun group -> group.Count >= int64 group.RowKeys.Length))

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
        |> mapResult
            (fun path content ->
                match content with
                | RegionContent.Table table when not (validTable table) ->
                    Error
                        {
                            Path = path
                            Reason =
                                "Table rows require unique keys, one value per column and disjoint groups covering their rows."
                        }
                | _ -> Ok content)
            id

    let private region =
        (record (fun role title content ->
            {
                Region.Role = role
                TitleId = title
                Content = content
            })
        : Fields<Region, _>)
        |> field "role" _.Role (VocabularyCodec.create RegionRole.vocabulary)
        |> optionalField "titleId" _.TitleId messageId
        |> field "content" _.Content content
        |> build
        |> named "Region"

    let view =
        (record (fun regions -> { Screen.Regions = regions }): Fields<Screen, _>)
        |> field "regions" _.Regions (list MaximumEntries region)
        |> build
        |> mapResult
            (fun path screen ->
                if Screen.valid screen.Regions then
                    Ok screen
                else
                    Error
                        {
                            Path = path
                            Reason = "Only FACTS may repeat within a screen."
                        })
            id
        |> named "Screen"

    let presented codec =
        (record (fun data view -> { Presented.Data = data; View = view })
        : Fields<Presented<'data>, _>)
        |> field "data" _.Data codec
        |> field "view" _.View view
