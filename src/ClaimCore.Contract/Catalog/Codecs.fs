namespace ClaimCore.Contract

open ClaimCore.Json
open ClaimCore.Contract.Protocol

open System
open System.Globalization

/// The catalog schema and its reader/writer share one typed codec.
module CatalogCodecs =
    open Codec

    let private count = integer 0L (int64 Int32.MaxValue) |> map int int64
    let private many codec = list Int32.MaxValue codec

    let private fieldName = VocabularyCodec.create FieldTable.vocabulary

    let private inputTarget = VocabularyCodec.create InputTargets.vocabulary

    let private caseStatus = VocabularyCodec.create CaseStatuses.vocabulary

    let private date =
        ResponseScalars.date
        |> map
            (fun value ->
                DateOnly.ParseExact(
                    value,
                    Formats.CalendarDateFormat,
                    CultureInfo.InvariantCulture
                ))
            (fun value -> value.ToString(Formats.CalendarDateFormat, CultureInfo.InvariantCulture))

    let private textFormat =
        (record (fun v0 v1 ->
            {
                TextFormatConstraints.MinimumCharacters = v0
                Grammar = v1
            })
        : Fields<TextFormatConstraints, _>)
        |> field "minimumCharacters" _.MinimumCharacters (count)
        |> field "grammar" _.Grammar (nullable text)
        |> build
        |> named "Presentation.TextFormatConstraints"

    let private textConstraints =
        (record (fun v0 v1 ->
            {
                ScalarTextConstraints.Format = v0
                MaximumCharacters = v1
            })
        : Fields<ScalarTextConstraints, _>)
        |> field "format" _.Format (textFormat)
        |> field "maximumCharacters" _.MaximumCharacters (nullable count)

    let private calendarDate =
        (record (fun v0 v1 v2 ->
            {
                CalendarDateScalarRule.ExactFormat = v0
                Minimum = v1
                Maximum = v2
            })
        : Fields<CalendarDateScalarRule, _>)
        |> field "exactFormat" _.ExactFormat (text)
        |> field "minimum" _.Minimum (date)
        |> field "maximum" _.Maximum (date)

    let private amount =
        (record (fun v0 v1 v2 v3 ->
            {
                AmountScalarRule.Text = v0
                Grammar = v1
                MaximumIntegerDigits = v2
                MaximumFractionalDigits = v3
            })
        : Fields<AmountScalarRule, _>)
        |> field "text" _.Text (textConstraints |> build)
        |> field "grammar" _.Grammar (text)
        |> field "maximumIntegerDigits" _.MaximumIntegerDigits (count)
        |> field "maximumFractionalDigits" _.MaximumFractionalDigits (count)

    let private currency =
        (record (fun v0 v1 v2 ->
            {
                CurrencyScalarRule.Text = v0
                Grammar = v1
                ExactCharacters = v2
            })
        : Fields<CurrencyScalarRule, _>)
        |> field "text" _.Text (textConstraints |> build)
        |> field "grammar" _.Grammar (text)
        |> field "exactCharacters" _.ExactCharacters (count)

    let private status =
        (record (fun v0 ->
            {
                CaseStatusScalarRule.AllowedValues = v0
            })
        : Fields<CaseStatusScalarRule, _>)
        |> field "allowedValues" _.AllowedValues (many caseStatus)

    let private scalar =
        tagged
            "Presentation.ScalarRule"
            [

                variant
                    "TEXT"
                    ScalarRule.Text
                    (function
                    | ScalarRule.Text value -> Some value
                    | _ -> None)
                    textConstraints

                variant
                    "CALENDAR_DATE"
                    ScalarRule.CalendarDate
                    (function
                    | ScalarRule.CalendarDate value -> Some value
                    | _ -> None)
                    calendarDate

                variant
                    "AMOUNT"
                    ScalarRule.Amount
                    (function
                    | ScalarRule.Amount value -> Some value
                    | _ -> None)
                    amount

                variant
                    "CURRENCY"
                    ScalarRule.Currency
                    (function
                    | ScalarRule.Currency value -> Some value
                    | _ -> None)
                    currency

                variant
                    "CASE_STATUS"
                    ScalarRule.CaseStatus
                    (function
                    | ScalarRule.CaseStatus value -> Some value
                    | _ -> None)
                    status

            ]

    let private businessField =
        (record (fun v0 v1 v2 v3 v4 v5 ->
            {
                FieldPresentationDefinition.Name = v0
                LabelId = v1
                MeaningId = v2
                Scalar = v3
                AllowsAbsence = v4
                Group = v5
            })
        : Fields<FieldPresentationDefinition, _>)
        |> field "name" _.Name (fieldName)
        |> field "labelId" _.LabelId (text)
        |> field "meaningId" _.MeaningId (text)
        |> field "scalar" _.Scalar (scalar)
        |> field "allowsAbsence" _.AllowsAbsence (boolean)
        |> field "group" _.Group (nullable (VocabularyCodec.create RecordGroup.vocabulary))
        |> build
        |> named "Presentation.FieldPresentationDefinition"

    let private inputField =
        (record (fun v0 v1 v2 v3 ->
            {
                InputPresentationDefinition.Name = v0
                LabelId = v1
                MeaningId = v2
                Scalar = v3
            })
        : Fields<InputPresentationDefinition, _>)
        |> field "name" _.Name text
        |> field "labelId" _.LabelId (text)
        |> field "meaningId" _.MeaningId (nullable text)
        |> field "scalar" _.Scalar (nullable scalar)
        |> build
        |> named "Presentation.InputPresentationDefinition"

    let private warnings =
        (record (fun v0 v1 ->
            {
                CharacterWarningDefinition.MaximumCodePoints = v0
                Categories = v1
            })
        : Fields<CharacterWarningDefinition, _>)
        |> field "maximumCodePoints" _.MaximumCodePoints (count)
        |> field "categories" _.Categories (many text)
        |> build
        |> named "Presentation.CharacterWarningDefinition"

    let private limits =
        (record (fun v0 v1 v2 ->
            {
                PresentationLimits.DefaultPageSize = v0
                MaximumPageSize = v1
                RequestByteLimit = v2
            })
        : Fields<PresentationLimits, _>)
        |> field "defaultPageSize" _.DefaultPageSize (count)
        |> field "maximumPageSize" _.MaximumPageSize (count)
        |> field "requestByteLimit" _.RequestByteLimit (count)
        |> build
        |> named "Presentation.PresentationLimits"

    let private bindingValue =
        choice
            "Presentation.BindingValue"
            [
                branch
                    BindingValue.Text
                    (function
                    | BindingValue.Text value -> Some value
                    | _ -> None)
                    text
                branch
                    BindingValue.Boolean
                    (function
                    | BindingValue.Boolean value -> Some value
                    | _ -> None)
                    boolean
                branch
                    BindingValue.Integer
                    (function
                    | BindingValue.Integer value -> Some value
                    | _ -> None)
                    (integer (-JsonValue.MaximumInteger) JsonValue.MaximumInteger)
            ]

    let private condition =
        (record (fun path value ->
            {
                BindingCondition.Path = path
                Value = value
            })
        : Fields<BindingCondition, _>)
        |> field "path" _.Path (many text)
        |> field "value" _.Value text
        |> build

    let private assignment =
        (record (fun path value branches ->
            {
                BindingAssignment.Path = path
                Value = value
                Branches = branches
            })
        : Fields<BindingAssignment, _>)
        |> field "path" _.Path (many text)
        |> field "value" _.Value bindingValue
        |> field "branches" _.Branches (many condition)
        |> build

    let private bindingKind =
        tagged
            "Presentation.BindingKind"
            [
                unitVariant "TEXT" BindingKind.Text
                unitVariant "INTEGER" BindingKind.Integer
                unitVariant "BOOLEAN" BindingKind.Boolean
                variant
                    "CHOICE"
                    BindingKind.Choice
                    (function
                    | BindingKind.Choice value -> Some value
                    | _ -> None)
                    (record id |> field "options" id (many text))
                variant
                    "GROUP"
                    BindingKind.Group
                    (function
                    | BindingKind.Group value -> Some value
                    | _ -> None)
                    (record id |> field "options" id (many text))
            ]

    let private inputSource =
        Vocabulary.define
            "inputSource"
            [
                InputSource.Authored, "AUTHORED"
                InputSource.OperationIdentity, "OPERATION_IDENTITY"
                InputSource.ExpectedRevision, "EXPECTED_REVISION"
            ]
        |> VocabularyCodec.create

    let private binding =
        record
            (fun
                identity
                field
                path
                kind
                branches
                label
                meaning
                order
                prefill
                required
                defaultValue
                targets
                source ->
                {
                    CatalogInput.Identity = identity
                    Field = field
                    Path = path
                    Kind = kind
                    Branches = branches
                    LabelId = label
                    MeaningId = meaning
                    Order = order
                    Prefill = prefill
                    Required = required
                    Default = defaultValue
                    DiagnosticTargets = targets
                    Source = source
                })
        |> field "identity" _.Identity text
        |> field "field" _.Field (nullable inputTarget)
        |> field "path" _.Path (many text)
        |> field "kind" _.Kind bindingKind
        |> field "branches" _.Branches (many condition)
        |> field "labelId" _.LabelId text
        |> field "meaningId" _.MeaningId (nullable text)
        |> field "order" _.Order count
        |> field "prefill" _.Prefill (nullable (many text))
        |> field "required" _.Required boolean
        |> field "default" _.Default (nullable bindingValue)
        |> field "diagnosticTargets" _.DiagnosticTargets (many text)
        |> field "source" _.Source inputSource
        |> build
        |> named "Presentation.CatalogInput"

    let entry =
        record (fun endpoint scope kind label meaning confirmation synopsis inputs constants ->
            {
                CatalogEntry.Endpoint = endpoint
                Subject = scope
                Kind = kind
                LabelId = label
                MeaningId = meaning
                ConfirmationId = confirmation
                Synopsis = synopsis
                Inputs = inputs
                Constants = constants
            })
        |> field "endpoint" _.Endpoint text
        |> field "subject" _.Subject text
        |> field "kind" _.Kind text
        |> field "labelId" _.LabelId text
        |> field "meaningId" _.MeaningId text
        |> field "confirmationId" _.ConfirmationId (nullable text)
        |> field "synopsis" _.Synopsis text
        |> field "inputs" _.Inputs (many binding)
        |> field "constants" _.Constants (many assignment)
        |> build
        |> named "Presentation.CatalogEntry"

    let codec =
        (record (fun fields inputs warnings limits entries version ->
            {
                CatalogDocument.Fields = fields
                InputFields = inputs
                CharacterWarnings = warnings
                Limits = limits
                Entries = entries
                ProductVersion = version
            })
        : Fields<CatalogDocument, _>)
        |> field "fields" _.Fields (many businessField)
        |> field "inputFields" _.InputFields (many inputField)
        |> field "characterWarnings" _.CharacterWarnings warnings
        |> field "limits" _.Limits limits
        |> field "entries" _.Entries (many entry)
        |> field "productVersion" _.ProductVersion text
        |> build
        |> named "Presentation.CatalogDocument"
