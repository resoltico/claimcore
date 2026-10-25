namespace ClaimCore.Contract

open System
open System.Globalization
open ClaimCore.Contract.Protocol

/// The descriptor schema and its reader/writer share one typed codec.
module DescriptorCodecs =
    open Codec

    let private count = integer 0L (int64 Int32.MaxValue) |> map int int64
    let private many codec = list Int32.MaxValue codec

    let private fieldName = vocabulary FieldTable.vocabulary

    let private inputTarget = vocabulary InputTargets.vocabulary

    let private caseStatus = vocabulary CaseStatuses.vocabulary

    let private caseKind = vocabulary CaseActKinds.vocabulary

    let private authorityKind =
        AuthorityKinds.all
        |> List.filter ((<>) AuthorityKind.ProvisionOwner)
        |> List.map (fun value -> value, AuthorityKinds.token value)
        |> enumeration

    let private role = vocabulary RoleTokens.vocabulary

    let private capability = vocabulary Capabilities.vocabulary

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

    let private textPolicy =
        (record (fun v0 v1 v2 v3 v4 ->
            {
                TextAdmissionPolicy.MaximumCharacters = v0
                RequiresNonBlank = v1
                RejectsSurroundingWhitespace = v2
                RejectsControlCharacters = v3
                RejectsForbiddenInvisibleCharacters = v4
            })
        : Fields<TextAdmissionPolicy, _>)
        |> field "maximumCharacters" _.MaximumCharacters (count)
        |> field "requiresNonBlank" _.RequiresNonBlank (boolean)
        |> field "rejectsSurroundingWhitespace" _.RejectsSurroundingWhitespace (boolean)
        |> field "rejectsControlCharacters" _.RejectsControlCharacters (boolean)
        |> field
            "rejectsForbiddenInvisibleCharacters"
            _.RejectsForbiddenInvisibleCharacters
            (boolean)
        |> build
        |> named "Presentation.TextAdmissionPolicy"

    let private textConstraints =
        (record (fun v0 v1 ->
            {
                ScalarTextConstraints.Format = v0
                Policy = v1
            })
        : Fields<ScalarTextConstraints, _>)
        |> field "format" _.Format (textFormat)
        |> field "policy" _.Policy (nullable textPolicy)

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
            "kind"
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

    let private input =
        tagged
            "Presentation.FieldInput"
            "prefill"
            [
                variant
                    "BLANK"
                    (fun value ->
                        {
                            FieldName = value
                            Prefill = PrefillSource.Blank
                        })
                    (fun value ->
                        match value.Prefill with
                        | PrefillSource.Blank -> Some value.FieldName
                        | _ -> None)
                    (record id |> field "fieldName" id inputTarget)
                variant
                    "CURRENT_FIELD"
                    (fun (field, current) ->
                        {
                            FieldName = field
                            Prefill = PrefillSource.CurrentField current
                        })
                    (fun value ->
                        match value.Prefill with
                        | PrefillSource.CurrentField current -> Some(value.FieldName, current)
                        | _ -> None)
                    (record (fun field current -> field, current)
                     |> field "fieldName" fst inputTarget
                     |> field "currentField" snd fieldName)
            ]

    let private correctionGroup =
        [
            CorrectionGroup.Registration
            CorrectionGroup.Decision
            CorrectionGroup.Payment
        ]
        |> List.map (fun value -> value, CorrectionGroup.token value)
        |> enumeration

    let private correctionAction =
        [
            CorrectionGroupAction.Keep
            CorrectionGroupAction.Replace
            CorrectionGroupAction.Clear
        ]
        |> List.map (fun value -> value, CorrectionActions.token value)
        |> enumeration

    let private group =
        (record (fun v0 v1 v2 v3 v4 ->
            {
                CorrectionGroupDefinition.Name = v0
                LabelId = v1
                MeaningId = v2
                Actions = v3
                ReplaceFields = v4
            })
        : Fields<CorrectionGroupDefinition, _>)
        |> field "name" _.Name (correctionGroup)
        |> field "labelId" _.LabelId (text)
        |> field "meaningId" _.MeaningId (text)
        |> field "actions" _.Actions (many correctionAction)
        |> field "replaceFields" _.ReplaceFields (many input)
        |> build
        |> named "Presentation.CorrectionGroupDefinition"

    let private inputs =
        tagged
            "Presentation.CommandInputs"
            "kind"
            [
                variant
                    "FIELDS"
                    CommandInputShape.Fields
                    (function
                    | CommandInputShape.Fields value -> Some value
                    | _ -> None)
                    (record id |> field "fields" id (many input))
                variant
                    "CORRECTION_GROUPS"
                    CommandInputShape.CorrectionGroups
                    (function
                    | CommandInputShape.CorrectionGroups value -> Some value
                    | _ -> None)
                    (record id |> field "groups" id (many group))
            ]

    let private command =
        (record (fun v0 v1 v2 v3 v4 ->
            {
                CommandDefinition.Kind = v0
                LabelId = v1
                MeaningId = v2
                ConfirmationId = v3
                Inputs = v4
            })
        : Fields<CommandDefinition, _>)
        |> field "kind" _.Kind (caseKind)
        |> field "labelId" _.LabelId (text)
        |> field "meaningId" _.MeaningId (text)
        |> field "confirmationId" _.ConfirmationId (text)
        |> field "inputs" _.Inputs (inputs)
        |> build
        |> named "Presentation.CommandDefinition"

    let private primitive =
        Vocabulary.define
            "primitive"
            [
                AuthorityInputPrimitive.Principal, "PRINCIPAL"
                AuthorityInputPrimitive.Role, "ROLE"
                AuthorityInputPrimitive.Scope, "SCOPE"
                AuthorityInputPrimitive.Boolean, "BOOLEAN"
            ]
        |> vocabulary

    let private authorityInput =
        (record (fun v0 v1 v2 ->
            {
                AuthorityInputDefinition.Name = v0
                LabelId = v1
                Primitive = v2
            })
        : Fields<AuthorityInputDefinition, _>)
        |> field "fieldName" _.Name (text)
        |> field "labelId" _.LabelId (text)
        |> field "primitive" _.Primitive (primitive)
        |> build
        |> named "Presentation.AuthorityInputDefinition"

    let private authorityCommand =
        (record (fun v0 v1 v2 v3 v4 ->
            {
                AuthorityOperationDefinition.Kind = v0
                LabelId = v1
                MeaningId = v2
                ConfirmationId = v3
                Inputs = v4
            })
        : Fields<AuthorityOperationDefinition, _>)
        |> field "kind" _.Kind (authorityKind)
        |> field "labelId" _.LabelId (text)
        |> field "meaningId" _.MeaningId (text)
        |> field "confirmationId" _.ConfirmationId (text)
        |> field "inputs" _.Inputs (many authorityInput)
        |> build
        |> named "Presentation.AuthorityOperationDefinition"

    let private businessField =
        (record (fun v0 v1 v2 v3 v4 ->
            {
                FieldPresentationDefinition.Name = v0
                LabelId = v1
                MeaningId = v2
                Scalar = v3
                AllowsAbsence = v4
            })
        : Fields<FieldPresentationDefinition, _>)
        |> field "name" _.Name (fieldName)
        |> field "labelId" _.LabelId (text)
        |> field "meaningId" _.MeaningId (text)
        |> field "scalar" _.Scalar (scalar)
        |> field "allowsAbsence" _.AllowsAbsence (boolean)
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
        |> field "name" _.Name (inputTarget)
        |> field "labelId" _.LabelId (text)
        |> field "meaningId" _.MeaningId (text)
        |> field "scalar" _.Scalar (scalar)
        |> build
        |> named "Presentation.InputPresentationDefinition"

    let private protocolField =
        (record (fun v0 v1 ->
            {
                ProtocolFieldDefinition.Name = v0
                LabelId = v1
            })
        : Fields<ProtocolFieldDefinition, _>)
        |> field "name" _.Name (text)
        |> field "labelId" _.LabelId (text)
        |> build
        |> named "Presentation.ProtocolFieldDefinition"

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

    let private parameter =
        (record (fun v0 v1 v2 ->
            {
                DiagnosticParameterDefinition.Name = v0
                Minimum = v1
                Maximum = v2
            })
        : Fields<DiagnosticParameterDefinition, _>)
        |> field "name" _.Name (text)
        |> field "minimum" _.Minimum (count)
        |> field "maximum" _.Maximum (count)
        |> build
        |> named "Presentation.DiagnosticParameterDefinition"

    let private diagnostic =
        (record (fun v0 v1 ->
            {
                DiagnosticDefinition.Id = v0
                Parameters = v1
            })
        : Fields<DiagnosticDefinition, _>)
        |> field "id" _.Id (text)
        |> field "parameters" _.Parameters (many parameter)
        |> build
        |> named "Presentation.DiagnosticDefinition"

    let private roleDefinition =
        (record (fun v0 v1 ->
            {
                RoleDefinition.Role = v0
                Capabilities = v1
            })
        : Fields<RoleDefinition, _>)
        |> field "role" _.Role (role)
        |> field "capabilities" _.Capabilities (many capability)
        |> build
        |> named "Presentation.RoleDefinition"

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
        (record (fun path value ->
            {
                BindingAssignment.Path = path
                Value = value
            })
        : Fields<BindingAssignment, _>)
        |> field "path" _.Path (many text)
        |> field "value" _.Value bindingValue
        |> build

    let private bindingKind =
        tagged
            "Presentation.BindingKind"
            "kind"
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

    let private argument =
        tagged
            "Presentation.InvocationArgument"
            "kind"
            [
                variant
                    "FLAG"
                    InvocationArgument.Flag
                    (function
                    | InvocationArgument.Flag value -> Some value
                    | _ -> None)
                    (record id |> field "name" id text)
                variant
                    "POSITIONAL"
                    InvocationArgument.Positional
                    (function
                    | InvocationArgument.Positional value -> Some value
                    | _ -> None)
                    (record id |> field "index" id count)
            ]

    let private inputSource =
        Vocabulary.define
            "inputSource"
            [
                InputSource.Authored, "AUTHORED"
                InputSource.OperationIdentity, "OPERATION_IDENTITY"
                InputSource.ExpectedRevision, "EXPECTED_REVISION"
            ]
        |> vocabulary

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
                targets
                source
                argument
                present ->
                {
                    InteractionInput.Identity = identity
                    Field = field
                    Path = path
                    Kind = kind
                    Branches = branches
                    LabelId = label
                    MeaningId = meaning
                    Order = order
                    Prefill = prefill
                    DiagnosticTargets = targets
                    Source = source
                    Argument = argument
                    WhenPresent = present
                })
        |> field "identity" _.Identity text
        |> field "field" _.Field (nullable inputTarget)
        |> field "path" _.Path (many text)
        |> field "kind" _.Kind bindingKind
        |> field "branches" _.Branches (many condition)
        |> field "labelId" _.LabelId text
        |> field "meaningId" _.MeaningId (nullable text)
        |> field "order" _.Order count
        |> field "prefill" _.Prefill (nullable fieldName)
        |> field "diagnosticTargets" _.DiagnosticTargets (many text)
        |> field "source" _.Source inputSource
        |> field "argument" _.Argument (nullable argument)
        |> field "whenPresent" _.WhenPresent (many assignment)
        |> build
        |> named "Presentation.InteractionInput"

    let private invocation =
        record (fun verb constants ->
            {
                InvocationDefinition.Verb = verb
                Constants = constants
            })
        |> field "verb" _.Verb text
        |> field "constants" _.Constants (many assignment)
        |> build

    let private interaction =
        record (fun scope kind inputs constants invocations ->
            {
                InteractionDefinition.Scope = scope
                Kind = kind
                Inputs = inputs
                Constants = constants
                Invocations = invocations
            })
        |> field "scope" _.Scope text
        |> field "kind" _.Kind text
        |> field "inputs" _.Inputs (many binding)
        |> field "constants" _.Constants (many assignment)
        |> field "invocations" _.Invocations (many invocation)
        |> build
        |> named "Presentation.InteractionDefinition"

    let codec =
        (record (fun v0 v1 v2 v3 v4 v5 v6 v7 v8 v9 v10 v11 v12 v13 ->
            {
                PresentationDescriptors.Fields = v0
                Commands = v1
                InputFields = v2
                AuthorityTarget = v3
                AuthorityCommands = v4
                AuthorityFields = v5
                CharacterWarnings = v6
                Statuses = v7
                RejectionDiagnostics = v8
                FaultDiagnostics = v9
                Roles = v10
                Limits = v11
                Interactions = v12
                ProductVersion = v13
            })
        : Fields<PresentationDescriptors, _>)
        |> field "fields" _.Fields (many businessField)
        |> field "commands" _.Commands (many command)
        |> field "inputFields" _.InputFields (many inputField)
        |> field "authorityTarget" _.AuthorityTarget (authorityInput)
        |> field "authorityCommands" _.AuthorityCommands (many authorityCommand)
        |> field "authorityFields" _.AuthorityFields (many protocolField)
        |> field "characterWarnings" _.CharacterWarnings (warnings)
        |> field "statuses" _.Statuses (many caseStatus)
        |> field "rejectionDiagnostics" _.RejectionDiagnostics (many diagnostic)
        |> field "faultDiagnostics" _.FaultDiagnostics (many diagnostic)
        |> field "roles" _.Roles (many roleDefinition)
        |> field "limits" _.Limits (limits)
        |> field "interactions" _.Interactions (many interaction)
        |> field "productVersion" _.ProductVersion (text)
        |> build
        |> named "Presentation.PresentationDescriptors"
