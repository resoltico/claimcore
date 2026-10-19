namespace ClaimCore.ContractGeneration

open ClaimCore.Wire
open ClaimCore.ContractGeneration.Presentation

open System
open System.Buffers
open System.Text
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Domain

module internal ContractJson =
    let contract write =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = true))
        write writer
        writer.Flush()
        let bytes = Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]

        bytes

/// Presentation names for protocol structure, separate from the thirteen business fields.
module internal ProtocolFieldPresentation =
    let authorityFields =
        [
            yield "operationId", "Operation ID"
            yield "expectedRevision", "Expected revision"
            yield "act", "Operation"
            yield "kind", "Type"
            yield "issuer", "Identity issuer"
            yield "subject", "Subject or client ID"
            let target = AuthorityOperationDefinitions.target
            yield target.Name, target.Label

            for definition in AuthorityOperationDefinitions.all do
                for input in definition.Inputs do
                    yield input.Name, input.Label
        ]
        |> List.distinct
        |> List.map (fun (name, label) -> name, label, "authority.field." + name + ".label")

module ContractDescriptors =
    let private textPolicy (writer: Utf8JsonWriter) (value: TextAdmissionPolicy) =
        writer.WriteStartObject()
        writer.WriteNumber("maximumCharacters", value.MaximumCharacters)
        writer.WriteBoolean("requiresNonBlank", value.RequiresNonBlank)
        writer.WriteBoolean("rejectsSurroundingWhitespace", value.RejectsSurroundingWhitespace)
        writer.WriteBoolean("rejectsControlCharacters", value.RejectsControlCharacters)

        writer.WriteBoolean(
            "rejectsForbiddenInvisibleCharacters",
            value.RejectsForbiddenInvisibleCharacters
        )

        writer.WriteEndObject()

    let private text (writer: Utf8JsonWriter) (value: ScalarTextConstraints) =
        writer.WritePropertyName("format")
        writer.WriteStartObject()
        writer.WriteNumber("minimumCharacters", value.Format.MinimumCharacters)

        match value.Format.Grammar with
        | Some grammar -> writer.WriteString("grammar", grammar)
        | None -> writer.WriteNull("grammar")

        writer.WriteEndObject()
        writer.WritePropertyName("policy")

        match value.Policy with
        | Some policy -> textPolicy writer policy
        | None -> writer.WriteNullValue()

    let private scalar (writer: Utf8JsonWriter) =
        function
        | ScalarRule.Text value ->
            writer.WriteString("kind", "TEXT")
            text writer value
        | ScalarRule.CalendarDate value ->
            writer.WriteString("kind", "CALENDAR_DATE")
            writer.WriteString("exactFormat", value.ExactFormat)
            writer.WriteString("minimum", value.Minimum.ToString("O"))
            writer.WriteString("maximum", value.Maximum.ToString("O"))
        | ScalarRule.Amount value ->
            writer.WriteString("kind", "AMOUNT")
            writer.WriteString("grammar", value.Grammar)
            writer.WriteNumber("maximumIntegerDigits", value.MaximumIntegerDigits)
            writer.WriteNumber("maximumFractionalDigits", value.MaximumFractionalDigits)
        | ScalarRule.Currency value ->
            writer.WriteString("kind", "CURRENCY")
            writer.WriteString("grammar", value.Grammar)
            writer.WriteNumber("exactCharacters", value.ExactCharacters)
        | ScalarRule.CaseStatus value ->
            writer.WriteString("kind", "CASE_STATUS")
            writer.WritePropertyName("allowedValues")
            writer.WriteStartArray()
            value.AllowedValues |> List.iter (CaseStatuses.token >> writer.WriteStringValue)
            writer.WriteEndArray()

    let private fields (writer: Utf8JsonWriter) =
        writer.WritePropertyName("fields")
        writer.WriteStartArray()

        for field in FieldDefinitions.all do
            writer.WriteStartObject()
            writer.WriteString("name", BusinessField.token field.Name)
            writer.WriteString("label", field.Label)
            writer.WriteString("meaning", field.Meaning)
            writer.WriteBoolean("allowsAbsence", field.AllowsAbsence)
            writer.WritePropertyName("scalar")
            writer.WriteStartObject()
            scalar writer field.Scalar
            writer.WriteEndObject()
            writer.WriteEndObject()

        writer.WriteEndArray()

    let private commands (writer: Utf8JsonWriter) =
        writer.WritePropertyName("commands")
        writer.WriteStartArray()

        for command in CommandDefinitions.all do
            writer.WriteStartObject()
            writer.WriteString("kind", CaseActKinds.token command.Kind)
            writer.WriteString("label", command.Label)
            writer.WriteString("meaning", command.Meaning)
            writer.WriteString("labelId", "command." + CaseActKinds.token command.Kind + ".label")

            writer.WriteString(
                "meaningId",
                "command." + CaseActKinds.token command.Kind + ".meaning"
            )

            CommandInputRendering.write writer command.Inputs
            writer.WriteEndObject()

        writer.WriteEndArray()

    let private inputFields (writer: Utf8JsonWriter) =
        writer.WritePropertyName("inputFields")
        writer.WriteStartArray()
        writer.WriteStartObject()
        writer.WriteString("name", "reason")
        writer.WriteString("label", "Stewardship reason")
        writer.WriteString("meaning", "Explain the record stewardship action.")
        writer.WritePropertyName("scalar")
        writer.WriteStartObject()
        scalar writer (FieldDefinitions.scalar InputField.Reason)
        writer.WriteEndObject()
        writer.WriteEndObject()
        writer.WriteEndArray()

    let private authorityPrimitive =
        function
        | AuthorityInputPrimitive.Principal -> "PRINCIPAL"
        | AuthorityInputPrimitive.Role -> "ROLE"
        | AuthorityInputPrimitive.Scope -> "SCOPE"
        | AuthorityInputPrimitive.Boolean -> "BOOLEAN"

    let private authorityInput (writer: Utf8JsonWriter) prefix (input: AuthorityInputDefinition) =
        writer.WriteStartObject()
        writer.WriteString("fieldName", input.Name)
        writer.WriteString("label", input.Label)
        writer.WriteString("primitive", authorityPrimitive input.Primitive)
        writer.WritePropertyName("path")
        writer.WriteStartArray()

        for part in prefix @ [ input.Name ] do
            writer.WriteStringValue(part: string)

        writer.WriteEndArray()
        writer.WriteEndObject()

    let private authorityFields (writer: Utf8JsonWriter) =
        writer.WritePropertyName("authorityFields")
        writer.WriteStartArray()

        for name, label, labelId in ProtocolFieldPresentation.authorityFields do
            writer.WriteStartObject()
            writer.WriteString("name", name)
            writer.WriteString("label", label)
            writer.WriteString("labelId", labelId)
            writer.WriteEndObject()

        writer.WriteEndArray()

    let private authority (writer: Utf8JsonWriter) =
        writer.WritePropertyName("authorityTarget")
        authorityInput writer [] AuthorityOperationDefinitions.target
        writer.WritePropertyName("authorityCommands")
        writer.WriteStartArray()

        for definition in AuthorityOperationDefinitions.all do
            writer.WriteStartObject()
            writer.WriteString("kind", AuthorityKinds.token definition.Kind)
            writer.WriteString("label", definition.Label)
            writer.WriteString("meaning", definition.Meaning)

            writer.WriteString(
                "labelId",
                "authority." + AuthorityKinds.token definition.Kind + ".label"
            )

            writer.WriteString(
                "meaningId",
                "authority." + AuthorityKinds.token definition.Kind + ".meaning"
            )

            writer.WritePropertyName("inputs")
            writer.WriteStartArray()
            definition.Inputs |> List.iter (authorityInput writer [ "act" ])
            writer.WriteEndArray()
            writer.WriteEndObject()

        writer.WriteEndArray()

    let private diagnostics (writer: Utf8JsonWriter) name definitions =
        writer.WritePropertyName(name: string)
        writer.WriteStartArray()

        for (value: DiagnosticDefinition) in definitions do
            writer.WriteStartObject()
            writer.WriteString("id", value.Id)
            writer.WritePropertyName("parameters")
            writer.WriteStartArray()

            for parameter in value.Parameters do
                writer.WriteStartObject()
                writer.WriteString("name", parameter.Name)
                writer.WriteNumber("minimum", parameter.Minimum)
                writer.WriteNumber("maximum", parameter.Maximum)
                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteEndObject()

        writer.WriteEndArray()

    let render () =
        ContractJson.contract (fun writer ->
            writer.WriteStartObject()
            fields writer
            commands writer
            inputFields writer
            authority writer
            authorityFields writer
            writer.WritePropertyName("statuses")
            writer.WriteStartArray()
            CaseStatuses.all |> List.iter (CaseStatuses.token >> writer.WriteStringValue)
            writer.WriteEndArray()
            diagnostics writer "rejectionDiagnostics" RejectionDiagnosticIds.definitions
            diagnostics writer "faultDiagnostics" CoreFaults.definitions
            writer.WritePropertyName("roles")
            writer.WriteStartArray()

            for role in Roles.all do
                writer.WriteStartObject()
                writer.WriteString("role", Roles.token role)
                writer.WritePropertyName("capabilities")
                writer.WriteStartArray()

                ActorAuthorization.capabilities role
                |> List.iter (Capabilities.token >> writer.WriteStringValue)

                writer.WriteEndArray()
                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WritePropertyName("limits")
            writer.WriteStartObject()
            writer.WriteNumber("defaultPageSize", ServiceLimits.DefaultPageSize)
            writer.WriteNumber("maximumPageSize", ServiceLimits.MaximumPageSize)
            writer.WriteNumber("requestByteLimit", ServiceLimits.RequestByteLimit)
            writer.WriteEndObject()
            writer.WriteEndObject())
