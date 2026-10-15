namespace ClaimCore.Contracts

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

        {
            Text = Encoding.UTF8.GetString bytes
            Bytes = bytes
        }

module ContractDescriptors =
    let private textPolicy (writer: Utf8JsonWriter) (value: TextAdmissionPolicy) =
        writer.WriteStartObject()
        writer.WriteNumber("maximumCharacters", value.MaximumCharacters)
        writer.WriteBoolean("requiresNonBlank", value.RequiresNonBlank)
        writer.WriteBoolean("rejectsSurroundingWhitespace", value.RejectsSurroundingWhitespace)
        writer.WriteBoolean("rejectsControlCharacters", value.RejectsControlCharacters)
        writer.WriteBoolean("rejectsFormattingCharacters", value.RejectsFormattingCharacters)
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
            writer.WriteString("name", field.Name)
            writer.WriteString("nativeName", field.NativeName)
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
            CommandInputRendering.write writer command.Inputs
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
