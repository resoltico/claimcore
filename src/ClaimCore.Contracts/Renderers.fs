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

    let private contract (write: Utf8JsonWriter -> unit) =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer, options)
        write writer
        writer.Flush()
        let bytes = Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]

        {
            Text = Encoding.UTF8.GetString(bytes)
            Bytes = bytes
        }

    let private writeSchemaDocument
        (writer: Utf8JsonWriter)
        (document: SchemaDocument)
        (schema: Schema)
        =
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

    let private writeSemantic (writer: Utf8JsonWriter) (semantic: SemanticCoreContract) =
        writer.WriteStartObject()
        writer.WriteString("contractKind", "SEMANTIC_CORE_V1")
        writer.WriteString("application", semantic.Application)
        writer.WriteString("scope", semantic.Scope)
        writer.WriteNumber("canonicalCommandFormat", semantic.CanonicalCommandFormat)
        writer.WriteNumber("requestFingerprintVersion", semantic.RequestFingerprintVersion)
        writer.WriteNumber("recoveryEnvelopeFormat", semantic.RecoveryEnvelopeFormat)
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
        writer.WriteEndObject()

    let private endpointSchemaDocument (input: Schema) =
        {
            Identifier = "https://claimcore.local/contracts/endpoint.schema.json"
            Title = "ClaimCore endpoint input"
            Root = input
            Definitions = []
        }

    let private writeCliEndpoint
        (writer: Utf8JsonWriter)
        (responses: Map<string, Schema>)
        (endpoint: CliEndpoint)
        =
        writer.WriteStartObject()
        writer.WriteString("id", endpoint.Identifier)
        writer.WriteBoolean("cancellable", endpoint.Cancellable)
        writer.WritePropertyName("inputSchema")
        writeSchemaDocument writer (endpointSchemaDocument endpoint.Input) endpoint.Input
        let response = Map.find endpoint.Identifier responses
        writer.WritePropertyName("responseSchema")
        writeSchemaDocument writer (endpointSchemaDocument response) response
        writer.WriteEndObject()

    let private writeCli (writer: Utf8JsonWriter) (projection: ContractModel) =
        writer.WriteStartObject()
        writer.WriteString("contractKind", "CLAIMCORE_CLI_V3")
        writer.WriteNumber("protocolVersion", 3)
        writer.WritePropertyName("definitionSchema")
        writeSchemaDocument writer projection.DefinitionSchema projection.DefinitionSchema.Root
        writer.WritePropertyName("endpoints")
        writer.WriteStartArray()

        projection.CliEndpoints
        |> List.iter (writeCliEndpoint writer projection.CliResponses)

        writer.WriteEndArray()
        writer.WriteEndObject()

    let private writeWebEndpoint
        (writer: Utf8JsonWriter)
        (projection: ContractModel)
        (endpoint: WebEndpoint)
        =
        writer.WriteStartObject()
        writer.WriteString("id", endpoint.Identifier)
        writer.WriteString("method", endpoint.Method)
        writer.WriteString("path", endpoint.Path)

        match endpoint.Body with
        | Some(JsonBody input) ->
            writer.WritePropertyName("body")
            writer.WriteStartObject()
            writer.WriteString("kind", "JSON")
            writer.WritePropertyName("schema")
            writeSchemaDocument writer (endpointSchemaDocument input) input
            writer.WriteEndObject()
        | Some(RawBody(mediaType, maximumBytes, headers)) ->
            writer.WritePropertyName("body")
            writer.WriteStartObject()
            writer.WriteString("kind", "RAW")
            writer.WriteString("mediaType", mediaType)
            writer.WriteNumber("maximumBytes", maximumBytes)
            writer.WritePropertyName("requiredHeaders")
            writer.WriteStartObject()

            headers
            |> List.iter (fun (name, schema) ->
                writer.WritePropertyName(name)
                writeSchemaDocument writer (endpointSchemaDocument schema) schema)

            writer.WriteEndObject()
            writer.WriteEndObject()
        | None -> ()

        writer.WritePropertyName("responseSchema")

        writeSchemaDocument
            writer
            (WebSchemas.responseDocument projection endpoint)
            endpoint.Response

        writer.WriteString(
            "responseDefinition",
            WebSchemas.responseDefinitionName endpoint.Identifier
        )

        match endpoint.SuccessMediaType with
        | Some mediaType -> writer.WriteString("successMediaType", mediaType)
        | None -> ()

        writer.WriteEndObject()

    let private writeWeb (writer: Utf8JsonWriter) (projection: ContractModel) =
        writer.WriteStartObject()
        writer.WriteString("contractKind", "CLAIMCORE_WEB_HTTP_V2")
        writer.WriteNumber("httpVersion", 2)
        writer.WritePropertyName("hostFailureStatuses")
        writer.WriteStartArray()
        WebSchemaDefinitions.hostFailureStatuses |> List.iter writer.WriteNumberValue
        writer.WriteEndArray()
        writer.WritePropertyName("definitionSchema")
        writeSchemaDocument writer projection.DefinitionSchema projection.DefinitionSchema.Root
        writer.WritePropertyName("endpoints")
        writer.WriteStartArray()
        projection.WebEndpoints |> List.iter (writeWebEndpoint writer projection)
        writer.WriteEndArray()
        writer.WriteEndObject()

    let semanticContract (semantic: SemanticCoreContract) =
        contract (fun writer -> writeSemantic writer semantic)

    let cliContract (projection: ContractModel) =
        contract (fun writer -> writeCli writer projection)

    let webContract (projection: ContractModel) =
        contract (fun writer -> writeWeb writer projection)
