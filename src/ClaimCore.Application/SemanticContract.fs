namespace ClaimCore.Application

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Builds the static semantic description directly from Domain-owned descriptors.
module SemanticContract =
    let private append (builder: StringBuilder) (value: string) =
        builder.Append(value).Append(char 0) |> ignore

    let private scalar builder rule =
        match rule with
        | ScalarRule.CalendarDate value ->
            append builder "CALENDAR_DATE"
            append builder value.ExactFormat
            append builder (value.Minimum.ToString("O", CultureInfo.InvariantCulture))
            append builder (value.Maximum.ToString("O", CultureInfo.InvariantCulture))
        | ScalarRule.Text value ->
            append builder "TEXT"
            append builder (string value.MinimumCharacters)
            append builder (string value.MaximumCharacters)
            append builder (string value.RequiresNonBlank)
            append builder (string value.RejectsSurroundingWhitespace)
            append builder (string value.RejectsControlCharacters)
            append builder (string value.RequiresWellFormedUnicode)
        | ScalarRule.Amount value ->
            append builder "AMOUNT"
            append builder value.Grammar
            append builder (string value.MaximumIntegerDigits)
            append builder (string value.MaximumFractionalDigits)
        | ScalarRule.Currency value ->
            append builder "CURRENCY"
            append builder value.Grammar
            append builder (string value.ExactCharacters)
        | ScalarRule.CaseStatus value ->
            append builder "CASE_STATUS"
            value.AllowedValues |> List.iter (CaseStatuses.token >> append builder)

    let current =
        {
            Application = BuildIdentity.current.Product
            Scope = "trusted-local-operator-claims-register"
            Fields = FieldDefinitions.all
            Commands = CommandDefinitions.all
            Statuses = CaseStatuses.all
            Rules = DomainRules.all
            DefaultPageSize = 50
            MaximumPageSize = 50
            RequestByteLimit = 65536
            CanonicalCommandFormat = RecordVersions.CanonicalCommandFormat
            RequestFingerprintVersion = RecordVersions.RequestFingerprint
            RecoveryEnvelopeFormat = 1
        }

    let fingerprint contract =
        let builder = StringBuilder()
        append builder contract.Application
        append builder contract.Scope
        append builder (string contract.DefaultPageSize)
        append builder (string contract.MaximumPageSize)
        append builder (string contract.RequestByteLimit)
        append builder (string contract.CanonicalCommandFormat)
        append builder (string contract.RequestFingerprintVersion)
        append builder (string contract.RecoveryEnvelopeFormat)

        contract.Fields
        |> List.iter (fun field ->
            append builder field.Name
            append builder field.NativeName
            append builder field.Label
            append builder field.Meaning
            append builder (string field.AllowsAbsence)
            scalar builder field.Scalar)

        contract.Commands
        |> List.iter (fun command ->
            append builder (CommandKinds.token command.Kind)
            append builder command.Label
            append builder command.Meaning

            command.Inputs
            |> List.iter (fun input ->
                append builder input.FieldName

                match input.Prefill with
                | PrefillSource.Blank -> append builder "BLANK"
                | PrefillSource.CurrentField field ->
                    append builder "CURRENT_FIELD"
                    append builder field))

        contract.Statuses |> List.iter (CaseStatuses.token >> append builder)

        contract.Rules
        |> List.iter (fun rule ->
            append builder rule.Identifier
            append builder rule.Meaning
            append builder (string rule.Category))

        builder.ToString()
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexStringLower
        |> SemanticCoreFingerprint.create
