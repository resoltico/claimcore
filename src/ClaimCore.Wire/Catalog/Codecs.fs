namespace ClaimCore.Wire

open ClaimCore.Wire
open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

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

    let private plainText =
        (record (fun maximumScalars maximumBytes allowsLineFeed ->
            {
                PlainTextConstraints.MaximumScalars = maximumScalars
                MaximumUtf8Bytes = maximumBytes
                AllowsLineFeed = allowsLineFeed
            })
        : Fields<PlainTextConstraints, _>)
        |> field "maximumScalars" _.MaximumScalars count
        |> field "maximumUtf8Bytes" _.MaximumUtf8Bytes count
        |> field "allowsLineFeed" _.AllowsLineFeed boolean

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

    let private textRules =
        [
            variant
                "PLAIN_TEXT"
                ScalarRule.PlainText
                (function
                | ScalarRule.PlainText value -> Some value
                | _ -> None)
                plainText
            variant
                "TEXT"
                ScalarRule.Text
                (function
                | ScalarRule.Text value -> Some value
                | _ -> None)
                textConstraints
        ]

    let private dateRules =
        [
            variant
                "CALENDAR_DATE"
                ScalarRule.CalendarDate
                (function
                | ScalarRule.CalendarDate value -> Some value
                | _ -> None)
                calendarDate
            variant
                "PLANNED_DATE"
                ScalarRule.PlannedDate
                (function
                | ScalarRule.PlannedDate value -> Some value
                | _ -> None)
                calendarDate
        ]

    let private moneyRules =
        [
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
        ]

    let private statusRules =
        [
            variant
                "CASE_STATUS"
                ScalarRule.CaseStatus
                (function
                | ScalarRule.CaseStatus value -> Some value
                | _ -> None)
                status
        ]

    let private scalar =
        tagged "Presentation.ScalarRule" (textRules @ dateRules @ moneyRules @ statusRules)

    let private businessField =
        (record (fun v0 valueKind v1 v2 rule v3 v4 v5 section pairs prominence listed ->
            {
                FieldPresentationDefinition.Name = v0
                ValueKind = valueKind
                LabelId = v1
                HintId = v2
                RuleId = rule
                Scalar = v3
                AllowsAbsence = v4
                Group = v5
                Section = section
                Pairs = pairs
                Prominence = prominence
                Listed = listed
            })
        : Fields<FieldPresentationDefinition, _>)
        |> field "name" _.Name (fieldName)
        |> field "valueKind" _.ValueKind (VocabularyCodec.create FieldValueKind.vocabulary)
        |> field "labelId" _.LabelId (text)
        |> field "hintId" _.HintId text
        |> field "ruleId" _.RuleId text
        |> field "scalar" _.Scalar (scalar)
        |> field "allowsAbsence" _.AllowsAbsence (boolean)
        |> field "group" _.Group (nullable (VocabularyCodec.create RecordGroup.vocabulary))
        |> field "section" _.Section (nullable (VocabularyCodec.create CaseSection.vocabulary))
        |> field "pairs" _.Pairs (nullable fieldName)
        |> field "prominence" _.Prominence (VocabularyCodec.create Prominence.vocabulary)
        |> field "listed" _.Listed boolean
        |> build
        |> named "Presentation.FieldPresentationDefinition"

    let private inputField =
        (record (fun v0 v1 v2 rule v3 ->
            {
                InputPresentationDefinition.Name = v0
                LabelId = v1
                HintId = v2
                RuleId = rule
                Scalar = v3
            })
        : Fields<InputPresentationDefinition, _>)
        |> field "name" _.Name text
        |> field "labelId" _.LabelId (text)
        |> field "hintId" _.HintId (nullable text)
        |> field "ruleId" _.RuleId (nullable text)
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
        (record (fun v0 v1 v2 window futureAllowance ->
            {
                PresentationLimits.DefaultPageSize = v0
                MaximumPageSize = v1
                RequestByteLimit = v2
                OperationWindowMilliseconds = window
                OperationFutureAllowanceMilliseconds = futureAllowance
            })
        : Fields<PresentationLimits, _>)
        |> field "defaultPageSize" _.DefaultPageSize (count)
        |> field "maximumPageSize" _.MaximumPageSize (count)
        |> field "requestByteLimit" _.RequestByteLimit (count)
        |> field "operationWindowMilliseconds" _.OperationWindowMilliseconds count
        |> field "operationFutureAllowanceMilliseconds" _.OperationFutureAllowanceMilliseconds count
        |> build
        |> named "Presentation.PresentationLimits"

    let private role =
        (record (fun role meaning kinds scopes ->
            {
                RolePresentationDefinition.Role = role
                MeaningId = meaning
                AdmittedPrincipalKinds = kinds
                AdmittedScopes = scopes
            })
        : Fields<RolePresentationDefinition, _>)
        |> field "role" _.Role (VocabularyCodec.create Role.vocabulary)
        |> field "meaningId" _.MeaningId text
        |> field
            "admittedPrincipalKinds"
            _.AdmittedPrincipalKinds
            (many (VocabularyCodec.create PrincipalKind.vocabulary))
        |> field
            "admittedScopes"
            _.AdmittedScopes
            (many (VocabularyCodec.create ScopeKind.vocabulary))
        |> build
        |> named "Presentation.RoleDefinition"

    let private capability =
        (record (fun capability kinds scopes ->
            {
                CapabilityPresentationDefinition.Capability = capability
                AdmittedPrincipalKinds = kinds
                AdmittedScopes = scopes
            })
        : Fields<CapabilityPresentationDefinition, _>)
        |> field "capability" _.Capability (VocabularyCodec.create Capability.vocabulary)
        |> field
            "admittedPrincipalKinds"
            _.AdmittedPrincipalKinds
            (many (VocabularyCodec.create PrincipalKind.vocabulary))
        |> field
            "admittedScopes"
            _.AdmittedScopes
            (many (VocabularyCodec.create ScopeKind.vocabulary))
        |> build
        |> named "Presentation.CapabilityDefinition"

    let private definitions =
        (record (fun fields inputs -> fields, inputs)
        : Fields<FieldPresentationDefinition list * InputPresentationDefinition list, _>)
        |> field "fields" fst (many businessField)
        |> field "inputFields" snd (many inputField)

    let private policies =
        (record (fun characterWarnings presentationLimits -> characterWarnings, presentationLimits)
        : Fields<CharacterWarningDefinition * PresentationLimits, _>)
        |> field "characterWarnings" fst warnings
        |> field "limits" snd limits

    let private currencyMinorUnit =
        (record (fun currency scale ->
            {
                CurrencyMinorUnitDefinition.Currency = currency
                MinorUnits = scale
            })
        : Fields<CurrencyMinorUnitDefinition, _>)
        |> field "currency" _.Currency ResponseScalars.currency
        |> field "minorUnits" _.MinorUnits (integer 0L 4L |> map int int64)
        |> build
        |> named "Presentation.CurrencyMinorUnit"

    let codec =
        (record
            (fun
                (fields, inputs)
                (characterWarnings, presentationLimits)
                roles
                capabilities
                moneyMinorUnits
                version ->
                {
                    CatalogDocument.Fields = fields
                    InputFields = inputs
                    CharacterWarnings = characterWarnings
                    Limits = presentationLimits
                    Roles = roles
                    Capabilities = capabilities
                    MoneyMinorUnits = moneyMinorUnits
                    ProductVersion = version
                })
        : Fields<CatalogDocument, _>)
        |> flatten (fun document -> document.Fields, document.InputFields) definitions
        |> flatten (fun document -> document.CharacterWarnings, document.Limits) policies
        |> field "roles" _.Roles (many role)
        |> field "capabilities" _.Capabilities (many capability)
        |> field "moneyMinorUnits" _.MoneyMinorUnits (many currencyMinorUnit)
        |> field "productVersion" _.ProductVersion text
        |> build
        |> named "Presentation.CatalogDocument"
