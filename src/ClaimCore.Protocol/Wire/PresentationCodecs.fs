namespace ClaimCore.Protocol

open System
open System.Text.RegularExpressions
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Protocol.Presentation

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
        max Role.all.Length (max Capability.all.Length (Vocabulary.all View.vocabulary).Length)

    let private textMaximum =
        max IncidentDescription.MaximumBytes HandlingSummary.MaximumBytes

    let private textValue = boundedText 0 textMaximum
    let private identifier = boundedText 1 textMaximum
    let private key = boundedText 1 MaximumKeyCharacters

    let private messageId =
        let pattern = "^[A-Za-z][A-Za-z0-9_.]*(?![\\s\\S])"

        boundedText 1 MaximumKeyCharacters
        |> constrained
            (Schema.String(Some pattern, None, Some 1, Some MaximumKeyCharacters))
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
        |> constrained (Schema.String(Some pattern, None, Some 1, Some maximum)) (fun value ->
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
            |> Option.defaultWith (fun () -> invalidOp "A presented code must belong to its declared vocabulary."))

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
            codeVariant many Role.vocabulary
            codeVariant many ScopeKind.vocabulary
            codeVariant many Capability.vocabulary
            codeVariant many ReminderState.vocabulary
            codeVariant many PresentedBoolean.vocabulary
        ]
        |> taggedOn "vocabulary" (if many then "PresentedCodes" else "PresentedCode")

    // This complete wire-tag table keeps every semantic constructor beside its closed payload grammar.
    // fsharplint:disable-next-line CyclomaticComplexity
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
