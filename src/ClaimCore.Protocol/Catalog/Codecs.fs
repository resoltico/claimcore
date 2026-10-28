namespace ClaimCore.Protocol

open ClaimCore.Protocol

open ClaimCore.Domain

open ClaimCore.Json

open System
open System.Globalization

/// The catalog schema and its reader/writer share one typed codec.
module CatalogCodecs =
    open Codec

    let private count = integer 0L (int64 Int32.MaxValue) |> map int int64
    let private many codec = list Int32.MaxValue codec

    let private fieldName = VocabularyCodec.create BusinessField.vocabulary

    let private inputTarget = VocabularyCodec.create InputTarget.vocabulary

    let private caseStatus = VocabularyCodec.create CaseStatus.vocabulary

    let private date =
        ResponseScalars.date
        |> map
            (fun value ->
                DateOnly.ParseExact(value, CalendarDate.ExactFormat, CultureInfo.InvariantCulture))
            (fun value -> value.ToString(CalendarDate.ExactFormat, CultureInfo.InvariantCulture))

    let private textFormat =
        (record (fun v0 v1 v2 ->
            {
                TextFormatConstraints.MinimumCharacters = v0
                Grammar = v1
                UnknownCode = v2
            })
        : Fields<TextFormatConstraints, _>)
        |> field "minimumCharacters" _.MinimumCharacters (count)
        |> field "grammar" _.Grammar (nullable text)
        |> optionalField "unknownCode" _.UnknownCode text
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
        (record (fun path value branches vocabulary ->
            {
                BindingAssignment.Path = path
                Value = value
                Branches = branches
                Vocabulary = vocabulary
            })
        : Fields<BindingAssignment, _>)
        |> field "path" _.Path (many text)
        |> field "value" _.Value bindingValue
        |> field "branches" _.Branches (many condition)
        |> field "vocabulary" _.Vocabulary (nullable text)
        |> build

    let private enumerationKindFields =
        record (fun vocabulary options -> vocabulary, options)
        |> field "vocabulary" fst text
        |> field "options" snd (many text)

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
                    | BindingKind.Choice(vocabulary, options) -> Some(vocabulary, options)
                    | _ -> None)
                    enumerationKindFields
                variant
                    "GROUP"
                    BindingKind.Group
                    (function
                    | BindingKind.Group(vocabulary, options) -> Some(vocabulary, options)
                    | _ -> None)
                    enumerationKindFields
            ]

    let private inputSource = InputSource.vocabulary |> VocabularyCodec.create

    let private binding =
        record
            (fun
                identity
                field
                path
                kind
                adviceGroup
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
                    AdviceGroup = adviceGroup
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
        |> field
            "adviceGroup"
            _.AdviceGroup
            (nullable (VocabularyCodec.create RecordGroup.vocabulary))
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

    let private identityName =
        function
        | CatalogOperation.Act act -> ActKind.token act
        | CatalogOperation.Query query -> ActorEndpoint.token (ActorEndpoint.Query query)

    type private EntryWire =
        {
            Endpoint: string
            Subject: CatalogSubject
            Kind: string
            LabelId: string
            MeaningId: string
            ConfirmationId: string option
            Inputs: CatalogInput list
            Constants: BindingAssignment list
        }

    let entry =
        record (fun endpoint subject kind label meaning confirmation inputs constants ->
            {
                EntryWire.Endpoint = endpoint
                Subject = subject
                Kind = kind
                LabelId = label
                MeaningId = meaning
                ConfirmationId = confirmation
                Inputs = inputs
                Constants = constants
            })
        |> field "endpoint" _.Endpoint text
        |> field "subject" _.Subject (VocabularyCodec.create CatalogSubject.vocabulary)
        |> field "kind" _.Kind text
        |> field "labelId" _.LabelId text
        |> field "meaningId" _.MeaningId text
        |> field "confirmationId" _.ConfirmationId (nullable text)
        |> field "inputs" _.Inputs (many binding)
        |> field "constants" _.Constants (many assignment)
        |> build
        |> mapResult
            (fun path wire ->
                let identity =
                    if
                        wire.Endpoint =
                            ActorEndpoint.token (ActorEndpoint.Operation OperationEndpoint.Execute)
                    then
                        ActKind.parse wire.Kind |> Option.map CatalogOperation.Act
                    else
                        ActorEndpoint.parse wire.Kind
                        |> Option.bind (function
                            | ActorEndpoint.Query query -> Some(CatalogOperation.Query query)
                            | _ -> None)

                match identity with
                | Some operation when
                    ActorEndpoint.token operation.Endpoint = wire.Endpoint
                    && operation.Subject = wire.Subject
                    ->
                    Ok
                        {
                            CatalogEntry.Kind = operation
                            LabelId = wire.LabelId
                            MeaningId = wire.MeaningId
                            ConfirmationId = wire.ConfirmationId
                            Inputs = wire.Inputs
                            Constants = wire.Constants
                        }
                | _ ->
                    Error
                        {
                            Path = path
                            Reason = "Invalid catalog operation identity."
                        })
            (fun (value: CatalogEntry) ->
                {
                    EntryWire.Endpoint = value.Endpoint
                    Subject = value.Subject
                    Kind = identityName value.Kind
                    LabelId = value.LabelId
                    MeaningId = value.MeaningId
                    ConfirmationId = value.ConfirmationId
                    Inputs = value.Inputs
                    Constants = value.Constants
                })
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
