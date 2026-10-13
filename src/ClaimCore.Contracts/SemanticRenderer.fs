namespace ClaimCore.Contracts

open System
open System.Buffers
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Domain

module internal ContractJson =
    let private options = JsonWriterOptions(Indented = false, SkipValidation = false)

    let contract (write: Utf8JsonWriter -> unit) =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer, options)
        write writer
        writer.Flush()
        let bytes = Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]

        {
            Text = Encoding.UTF8.GetString(bytes)
            Bytes = bytes
        }

    let writeSchemaDocument (writer: Utf8JsonWriter) (document: SchemaDocument) (schema: Schema) =
        let rendered = CanonicalJson.renderSchema document schema

        use parsed = JsonDocument.Parse(CanonicalContract.bytes rendered)
        parsed.RootElement.WriteTo(writer)

    let private scalar (writer: Utf8JsonWriter) (scalar: ScalarRule) =
        writer.WriteStartObject()

        match scalar with
        | ScalarRule.CalendarDate rule ->
            writer.WriteString("kind", "CALENDAR_DATE")
            writer.WriteString("exactFormat", rule.ExactFormat)
            writer.WriteString("minimum", rule.Minimum.ToString("O"))
            writer.WriteString("maximum", rule.Maximum.ToString("O"))
        | ScalarRule.Text rules ->
            writer.WriteString("kind", "TEXT")
            writer.WriteNumber("minimumCharacters", rules.MinimumCharacters)
            writer.WriteNumber("maximumCharacters", rules.MaximumCharacters)
            writer.WriteBoolean("requiresNonBlank", rules.RequiresNonBlank)
            writer.WriteBoolean("rejectsSurroundingWhitespace", rules.RejectsSurroundingWhitespace)
            writer.WriteBoolean("rejectsControlCharacters", rules.RejectsControlCharacters)
            writer.WriteBoolean("requiresWellFormedUnicode", rules.RequiresWellFormedUnicode)
        | ScalarRule.Amount rules ->
            writer.WriteString("kind", "AMOUNT")
            writer.WriteString("grammar", rules.Grammar)
            writer.WriteNumber("maximumIntegerDigits", rules.MaximumIntegerDigits)
            writer.WriteNumber("maximumFractionalDigits", rules.MaximumFractionalDigits)
        | ScalarRule.Currency rules ->
            writer.WriteString("kind", "CURRENCY")
            writer.WriteString("grammar", rules.Grammar)
            writer.WriteNumber("exactCharacters", rules.ExactCharacters)
        | ScalarRule.CaseStatus rules ->
            writer.WriteString("kind", "CASE_STATUS")
            writer.WritePropertyName("allowedValues")
            writer.WriteStartArray()
            rules.AllowedValues |> List.iter (CaseStatuses.token >> writer.WriteStringValue)
            writer.WriteEndArray()

        writer.WriteEndObject()

    let private writeFields (writer: Utf8JsonWriter) (fields: FieldDefinition list) =
        writer.WritePropertyName("fields")
        writer.WriteStartArray()

        fields
        |> List.iter (fun field ->
            writer.WriteStartObject()
            writer.WriteString("name", field.Name)
            writer.WriteString("nativeName", field.NativeName)
            writer.WriteString("label", field.Label)
            writer.WriteString("meaning", field.Meaning)
            writer.WriteBoolean("allowsAbsence", field.AllowsAbsence)
            writer.WritePropertyName("scalar")
            scalar writer field.Scalar
            writer.WriteEndObject())

        writer.WriteEndArray()

    let private writeCommands (writer: Utf8JsonWriter) (commands: CommandDefinition list) =
        writer.WritePropertyName("commands")
        writer.WriteStartArray()

        commands
        |> List.iter (fun command ->
            writer.WriteStartObject()
            writer.WriteString("kind", CommandKinds.token command.Kind)
            writer.WriteString("label", command.Label)
            writer.WriteString("meaning", command.Meaning)
            SemanticInputRenderer.write writer command.Inputs
            writer.WriteEndObject())

        writer.WriteEndArray()

    let private writeRules (writer: Utf8JsonWriter) (rules: DomainRuleDefinition list) =
        writer.WritePropertyName("rules")
        writer.WriteStartArray()

        rules
        |> List.iter (fun rule ->
            writer.WriteStartObject()
            writer.WriteString("identifier", rule.Identifier)

            let category =
                match rule.Category with
                | DomainRuleCategory.CrossField -> "CROSS_FIELD"
                | DomainRuleCategory.Transition -> "TRANSITION"

            writer.WriteString("category", category)
            writer.WriteString("meaning", rule.Meaning)
            writer.WriteEndObject())

        writer.WriteEndArray()

    let private writeDiagnostics (writer: Utf8JsonWriter) (name: string) definitions =
        writer.WritePropertyName(name)
        writer.WriteStartArray()

        definitions
        |> List.iter (fun (definition: DiagnosticDefinition) ->
            writer.WriteStartObject()
            writer.WriteString("id", definition.Id)
            writer.WritePropertyName("parameters")
            writer.WriteStartArray()

            definition.Parameters
            |> List.iter (fun parameter ->
                writer.WriteStartObject()
                writer.WriteString("name", parameter.Name)
                writer.WriteNumber("minimum", parameter.Minimum)
                writer.WriteNumber("maximum", parameter.Maximum)
                writer.WriteEndObject())

            writer.WriteEndArray()
            writer.WriteEndObject())

        writer.WriteEndArray()

    let private writeSemantic (writer: Utf8JsonWriter) (semantic: SemanticCoreContract) =
        writer.WriteStartObject()
        writer.WriteString("contractKind", "SEMANTIC_CORE_V1")
        writer.WriteString("application", semantic.Application)
        writer.WriteString("scope", semantic.Scope)
        writer.WriteNumber("ruleSetVersion", semantic.RuleSetVersion)
        writer.WriteNumber("canonicalCommandFormat", semantic.CanonicalCommandFormat)
        writer.WriteNumber("requestFingerprintVersion", semantic.RequestFingerprintVersion)
        writer.WriteNumber("ledgerEntryFormat", semantic.LedgerEntryFormat)
        writer.WriteNumber("defaultPageSize", semantic.DefaultPageSize)
        writer.WriteNumber("maximumPageSize", semantic.MaximumPageSize)
        writer.WriteNumber("requestByteLimit", semantic.RequestByteLimit)
        writeFields writer semantic.Fields
        writeCommands writer semantic.Commands
        writer.WritePropertyName("statuses")
        writer.WriteStartArray()
        semantic.Statuses |> List.iter (CaseStatuses.token >> writer.WriteStringValue)
        writer.WriteEndArray()
        writeRules writer semantic.Rules
        writeDiagnostics writer "rejectionDiagnostics" semantic.RejectionDiagnostics
        writeDiagnostics writer "faultDiagnostics" semantic.FaultDiagnostics
        writer.WriteEndObject()

    let semanticContract semantic =
        contract (fun writer -> writeSemantic writer semantic)
