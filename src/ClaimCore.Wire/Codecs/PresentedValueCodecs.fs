namespace ClaimCore.Wire

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Presentation

module PresentedValueCodecs =
    open Codec
    open ResponseScalars
    open PresentationKeys

    let MaximumEntries =
        max
            (CaseFields.declarations.Length + 2)
            (2 * PeopleAdmission.MaximumGrants * 3 + Capability.all.Length * 3 + 3)

    let MaximumCodes =
        max TaskVocabulary.all.Length (max Role.all.Length Capability.all.Length)

    let private textMaximum =
        max IncidentDescription.MaximumBytes HandlingSummary.MaximumBytes

    let private textValue = boundedText 0 textMaximum
    let private identifier = boundedText 1 textMaximum

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
            DecimalTotal.MaximumIntegerDigits + DecimalTotal.MaximumFractionalDigits + 2

        let pattern = "^(?:" + DecimalTotal.signedGrammar + ")(?![\\s\\S])"

        patternedText 1 maximum pattern None
        |> parsed DecimalTotal.parseSigned DecimalTotal.format

    let internal moneyFields =
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

    let internal codes many =
        [
            codeVariant many Lifecycle.vocabulary
            codeVariant many CaseStatus.vocabulary
            codeVariant many ClosureOutcome.vocabulary
            codeVariant many DaysOpenBand.vocabulary
            codeVariant many MissingCaseGroup.vocabulary
            codeVariant many CaseProgress.vocabulary
            codeVariant many PaymentDirection.vocabulary
            codeVariant many PartyRole.vocabulary
            codeVariant many CaseAssignment.vocabulary
            codeVariant many Role.vocabulary
            codeVariant many ScopeKind.vocabulary
            codeVariant many Capability.vocabulary
            codeVariant many DisclosureKind.vocabulary
            codeVariant many DisclosureOutcome.vocabulary
            codeVariant many ReminderState.vocabulary
            codeVariant many PresentedBoolean.vocabulary
            codeVariant many CorrectionGroupAction.vocabulary
            codeVariant many TaskVocabulary.vocabulary
            codeVariant many CaseStageKind.vocabulary
            codeVariant many CaseAttention.vocabulary
            codeVariant many AccountState.vocabulary
            codeVariant many PrincipalKind.vocabulary
            codeVariant many RequesterKind.vocabulary
            codeVariant many ClaimCore.Application.Contracts.View.vocabulary
            codeVariant many CaseDue.vocabulary
            codeVariant many ReminderDue.vocabulary
            codeVariant many ReminderKind.vocabulary
            codeVariant many DeadlineOutcome.vocabulary
        ]
        |> taggedOn "vocabulary" (if many then "PresentedCodes" else "PresentedCode")

    let private presentedCode =
        codes false
        |> map
            (function
            | PresentedValue.Code(vocabulary, token) -> vocabulary, token
            | _ -> invalidOp "The presentation payload requires a code.")
            (fun (vocabulary, token) -> PresentedValue.Code(vocabulary, token))

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
                    "DATE_WITH_CODE"
                    PresentedValue.DateWithCode
                    (function
                    | PresentedValue.DateWithCode(value, code) -> Some(value, code)
                    | _ -> None)
                    (record (fun value code -> value, code)
                     |> field "value" fst calendarDate
                     |> field "code" snd presentedCode)
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
                    (fun (state, detail) -> PresentedValue.State(state, detail))
                    (function
                    | PresentedValue.State(state, detail) -> Some(state, detail)
                    | _ -> None)
                    (record (fun state detail -> state, detail)
                     |> fixedField "vocabulary" (Vocabulary.name CaseHeadline.vocabulary)
                     |> field "token" fst (VocabularyCodec.create CaseHeadline.vocabulary)
                     |> optionalField "detail" snd presentedCode)
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

    let internal change =
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
        (record (fun changes unchanged notEntered ->
            {
                ReviewPresentation.Changes = changes
                Unchanged = unchanged
                NotEntered = defaultArg notEntered []
            })
        : Fields<ReviewPresentation, _>)
        |> field "changes" _.Changes (list MaximumEntries change)
        |> field "unchanged" _.Unchanged (list MaximumEntries unchanged)
        |> optionalField
            "notEntered"
            (fun value ->
                if value.NotEntered.IsEmpty then
                    None
                else
                    Some value.NotEntered)
            (list MaximumEntries messageId)
        |> build
        |> named "ReviewPresentation"
