namespace ClaimCore.Contracts

open ClaimCore.Application
open ClaimCore.Domain

module internal ProjectionSchema =
    let private textConstant value = Schema.constant (TextConstant value)

    let private integerConstant value =
        Schema.constant (IntegerConstant(int64 value))

    let private booleanConstant value = Schema.constant (BooleanConstant value)
    let private objectOf properties = Schema.objectOf false properties

    let private calendarDefinition (value: CalendarDateScalarRule) =
        objectOf
            [
                Schema.property "kind" (textConstant "CALENDAR_DATE") true
                Schema.property "exactFormat" (textConstant value.ExactFormat) true
                Schema.property "minimum" (textConstant (value.Minimum.ToString("O"))) true
                Schema.property "maximum" (textConstant (value.Maximum.ToString("O"))) true
            ]

    let private textDefinition (value: ScalarTextConstraints) =
        objectOf
            [
                Schema.property "kind" (textConstant "TEXT") true
                Schema.property "minimumCharacters" (integerConstant value.MinimumCharacters) true
                Schema.property "maximumCharacters" (integerConstant value.MaximumCharacters) true
                Schema.property "requiresNonBlank" (booleanConstant value.RequiresNonBlank) true
                Schema.property
                    "rejectsSurroundingWhitespace"
                    (booleanConstant value.RejectsSurroundingWhitespace)
                    true
                Schema.property
                    "rejectsControlCharacters"
                    (booleanConstant value.RejectsControlCharacters)
                    true
                Schema.property
                    "requiresWellFormedUnicode"
                    (booleanConstant value.RequiresWellFormedUnicode)
                    true
            ]

    let private amountDefinition (value: AmountScalarRule) =
        objectOf
            [
                Schema.property "kind" (textConstant "AMOUNT") true
                Schema.property "grammar" (textConstant value.Grammar) true
                Schema.property
                    "maximumIntegerDigits"
                    (integerConstant value.MaximumIntegerDigits)
                    true
                Schema.property
                    "maximumFractionalDigits"
                    (integerConstant value.MaximumFractionalDigits)
                    true
            ]

    let private currencyDefinition (value: CurrencyScalarRule) =
        objectOf
            [
                Schema.property "kind" (textConstant "CURRENCY") true
                Schema.property "grammar" (textConstant value.Grammar) true
                Schema.property "exactCharacters" (integerConstant value.ExactCharacters) true
            ]

    let private statusDefinition (value: CaseStatusScalarRule) =
        objectOf
            [
                Schema.property "kind" (textConstant "CASE_STATUS") true
                Schema.property
                    "allowedValues"
                    (value.AllowedValues
                     |> List.map (CaseStatuses.token >> TextConstant >> Schema.constant)
                     |> Schema.tuple)
                    true
            ]

    let private scalarDefinition (scalar: ScalarRule) =
        match scalar with
        | ScalarRule.CalendarDate value -> calendarDefinition value
        | ScalarRule.Text value -> textDefinition value
        | ScalarRule.Amount value -> amountDefinition value
        | ScalarRule.Currency value -> currencyDefinition value
        | ScalarRule.CaseStatus value -> statusDefinition value

    let fieldDefinition (field: FieldDefinition) =
        Schema.objectOf
            false
            [
                Schema.property "name" (textConstant field.Name) true
                Schema.property "nativeName" (textConstant field.NativeName) true
                Schema.property "label" (Schema.string None None (Some 1) (Some 4096)) true
                Schema.property "meaning" (Schema.string None None (Some 1) (Some 4096)) true
                Schema.property "allowsAbsence" (booleanConstant field.AllowsAbsence) true
                Schema.property "scalar" (scalarDefinition field.Scalar) true
            ]

    let private exactArray (schemas: Schema list) (length: int) =
        if schemas.Length <> length then
            invalidArg (nameof length) "Exact schema array length does not match its items."

        Schema.tuple schemas

    let commandDefinition (command: CommandDefinition) =
        Schema.objectOf
            false
            [
                Schema.property "kind" (textConstant (CommandKinds.token command.Kind)) true
                Schema.property "label" (Schema.string None None (Some 1) (Some 4096)) true
                Schema.property "meaning" (Schema.string None None (Some 1) (Some 4096)) true
                Schema.property "inputs" (CommandInputProjection.definition command.Inputs) true
            ]

    let private ruleDefinition (rule: DomainRuleDefinition) =
        let category =
            match rule.Category with
            | DomainRuleCategory.CrossField -> "CROSS_FIELD"
            | DomainRuleCategory.Transition -> "TRANSITION"

        Schema.objectOf
            false
            [
                Schema.property "identifier" (textConstant rule.Identifier) true
                Schema.property "category" (textConstant category) true
                Schema.property "meaning" (Schema.string None None (Some 1) (Some 4096)) true
            ]

    let private command (fields: FieldDefinition list) (definition: CommandDefinition) =
        CommandInputProjection.payload fields definition

    let commandDraft (semantic: SemanticCoreContract) =
        let caseReference =
            semantic.Fields |> List.find (fun field -> field.Name = "caseReference")

        Schema.objectOf
            false
            [
                Schema.property "operationId" ScalarSchemas.uuid true
                Schema.property "caseReference" (ScalarSchemas.scalar caseReference.Scalar) true
                Schema.property "expectedRevision" ScalarSchemas.decimalText true
                Schema.property
                    "command"
                    (semantic.Commands |> List.map (command semantic.Fields) |> Schema.oneOf)
                    true
            ]

    let private diagnostics (semantic: SemanticCoreContract) =
        [
            "rejectionDiagnostics", semantic.RejectionDiagnostics
            "faultDiagnostics", semantic.FaultDiagnostics
            "recoveryDiagnostics", semantic.RecoveryDiagnostics
        ]
        |> List.map (fun (name, definitions) ->
            Schema.property
                name
                (definitions |> List.map RejectionDiagnosticSchemas.definition |> Schema.tuple)
                true)

    let private definitionRoot
        (semantic: SemanticCoreContract)
        (fields: Schema)
        (commands: Schema)
        (statusValues: Schema)
        (rules: Schema)
        =
        [
            Schema.property "contractKind" (textConstant "SEMANTIC_CORE_V1") true
            Schema.property "application" (textConstant semantic.Application) true
            Schema.property "scope" (textConstant semantic.Scope) true
            Schema.property "ruleSetVersion" (integerConstant semantic.RuleSetVersion) true
            Schema.property
                "canonicalCommandFormat"
                (integerConstant semantic.CanonicalCommandFormat)
                true
            Schema.property
                "requestFingerprintVersion"
                (integerConstant semantic.RequestFingerprintVersion)
                true
            Schema.property
                "recoveryEnvelopeFormat"
                (integerConstant semantic.RecoveryEnvelopeFormat)
                true
            Schema.property "defaultPageSize" (integerConstant semantic.DefaultPageSize) true
            Schema.property "maximumPageSize" (integerConstant semantic.MaximumPageSize) true
            Schema.property "requestByteLimit" (integerConstant semantic.RequestByteLimit) true
            Schema.property "fields" fields true
            Schema.property "commands" commands true
            Schema.property "statuses" statusValues true
            Schema.property "rules" rules true
        ]
        |> List.append (diagnostics semantic)
        |> Schema.objectOf false

    let definitionDocument (semantic: SemanticCoreContract) =
        let fields =
            semantic.Fields
            |> List.map fieldDefinition
            |> fun values -> exactArray values semantic.Fields.Length

        let commands =
            semantic.Commands
            |> List.map commandDefinition
            |> fun values -> exactArray values semantic.Commands.Length

        let statusValues =
            semantic.Statuses
            |> List.map (CaseStatuses.token >> TextConstant >> Schema.constant)
            |> Schema.tuple

        let rules =
            semantic.Rules
            |> List.map ruleDefinition
            |> fun values -> exactArray values semantic.Rules.Length

        let root = definitionRoot semantic fields commands statusValues rules

        {
            Identifier = "https://claimcore.local/contracts/semantic-core.schema.json"
            Title = "ClaimCore semantic core contract"
            Root = root
            Definitions = []
        }
