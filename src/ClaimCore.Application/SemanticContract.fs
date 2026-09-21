namespace ClaimCore.Application

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Machine identity excludes presentation copy and includes every declared scalar constraint.
module SemanticContract =
    let private integer (value: int) =
        value.ToString(CultureInfo.InvariantCulture)

    // Length-prefix every token, including collection sizes, so field and collection boundaries
    // cannot collide. This is an identity format, not a human-facing serialization.
    let private append (builder: StringBuilder) (value: string) =
        builder.Append(integer value.Length).Append(':').Append(value) |> ignore

    let private number builder value = append builder (integer value)

    let private boolean builder value =
        append builder (if value then "true" else "false")

    let private sequence builder write values =
        number builder (List.length values)
        values |> List.iter (write builder)

    let private textConstraints builder (value: ScalarTextConstraints) =
        number builder value.MinimumCharacters
        number builder value.MaximumCharacters
        boolean builder value.RequiresNonBlank
        boolean builder value.RejectsSurroundingWhitespace
        boolean builder value.RejectsControlCharacters
        boolean builder value.RequiresWellFormedUnicode

    let private scalar builder rule =
        match rule with
        | ScalarRule.CalendarDate value ->
            append builder "CALENDAR_DATE"
            append builder value.ExactFormat
            append builder (value.Minimum.ToString("O", CultureInfo.InvariantCulture))
            append builder (value.Maximum.ToString("O", CultureInfo.InvariantCulture))
        | ScalarRule.Text value ->
            append builder "TEXT"
            textConstraints builder value
        | ScalarRule.Amount value ->
            append builder "AMOUNT"
            textConstraints builder value.Text
            append builder value.Grammar
            number builder value.MaximumIntegerDigits
            number builder value.MaximumFractionalDigits
        | ScalarRule.Currency value ->
            append builder "CURRENCY"
            textConstraints builder value.Text
            append builder value.Grammar
            number builder value.ExactCharacters
        | ScalarRule.CaseStatus value ->
            append builder "CASE_STATUS"

            sequence
                builder
                (fun output item -> append output (CaseStatuses.token item))
                value.AllowedValues

    let private input builder (value: FieldInputDefinition) =
        append builder value.FieldName

        match value.Prefill with
        | PrefillSource.Blank -> append builder "BLANK"
        | PrefillSource.CurrentField field ->
            append builder "CURRENT_FIELD"
            append builder field

    let private groupAction builder action =
        match action with
        | CorrectionGroupAction.Keep -> append builder "KEEP"
        | CorrectionGroupAction.Replace -> append builder "REPLACE"
        | CorrectionGroupAction.Clear -> append builder "CLEAR"

    let private group builder (value: CorrectionGroupDefinition) =
        append builder value.Name
        sequence builder groupAction value.Actions
        sequence builder input value.ReplaceFields

    let private inputShape builder shape =
        match shape with
        | CommandInputShape.Fields inputs ->
            append builder "FIELDS"
            sequence builder input inputs
        | CommandInputShape.CorrectionGroups groups ->
            append builder "CORRECTION_GROUPS"
            sequence builder group groups

    let current =
        {
            Application = BuildIdentity.current.Product
            Scope = "trusted-local-operator-claims-register"
            RuleSetVersion = DomainRules.version
            Fields = FieldDefinitions.all
            Commands = CommandDefinitions.all
            Statuses = CaseStatuses.all
            Rules = DomainRules.all
            RejectionDiagnostics = RejectionDiagnosticIds.definitions
            DefaultPageSize = 50
            MaximumPageSize = 50
            RequestByteLimit = 65536
            CanonicalCommandFormat = RecordVersions.CanonicalCommandFormat
            RequestFingerprintVersion = RecordVersions.RequestFingerprint
            RecoveryEnvelopeFormat = 1
        }

    let private diagnostic output (value: RejectionDiagnosticDefinition) =
        append output value.Id

        sequence
            output
            (fun target (parameter: DiagnosticParameterDefinition) ->
                append target parameter.Name
                number target parameter.Minimum
                number target parameter.Maximum)
            value.Parameters

    /// A descriptor digest is not a proof that two arbitrary implementations behave identically.
    /// RuleSetVersion accounts for deliberate behavior changes not represented by descriptor data.
    let fingerprint (contract: SemanticCoreContract) =
        let builder = StringBuilder()
        append builder "CLAIMCORE_SEMANTIC_IDENTITY_V2"
        append builder contract.Application
        append builder contract.Scope
        number builder contract.RuleSetVersion
        number builder contract.DefaultPageSize
        number builder contract.MaximumPageSize
        number builder contract.RequestByteLimit
        number builder contract.CanonicalCommandFormat
        number builder contract.RequestFingerprintVersion
        number builder contract.RecoveryEnvelopeFormat

        sequence
            builder
            (fun output (field: FieldDefinition) ->
                append output field.Name
                append output field.NativeName
                boolean output field.AllowsAbsence
                scalar output field.Scalar)
            contract.Fields

        sequence
            builder
            (fun output (command: CommandDefinition) ->
                append output (CommandKinds.token command.Kind)
                inputShape output command.Inputs)
            contract.Commands

        sequence
            builder
            (fun output status -> append output (CaseStatuses.token status))
            contract.Statuses

        sequence
            builder
            (fun output (rule: DomainRuleDefinition) ->
                append output rule.Identifier

                match rule.Category with
                | DomainRuleCategory.CrossField -> append output "CROSS_FIELD"
                | DomainRuleCategory.Transition -> append output "TRANSITION")
            contract.Rules

        sequence builder diagnostic contract.RejectionDiagnostics

        builder.ToString()
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexStringLower
        |> SemanticCoreFingerprint.create
