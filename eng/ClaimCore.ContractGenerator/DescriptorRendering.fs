namespace ClaimCore.ContractGeneration

open ClaimCore.Contract

open ClaimCore.Contract.Protocol

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

/// Presentation names for protocol structure, separate from the business fields.
module internal ProtocolFieldPresentation =
    let authorityFields
        (target: AuthorityInputDefinition)
        (commands: AuthorityOperationDefinition list)
        =
        [
            yield "operationId", "authority.field.operationId.label"
            yield "expectedRevision", "authority.field.expectedRevision.label"
            yield "act", "authority.field.act.label"
            yield "kind", "authority.field.kind.label"
            yield "issuer", "authority.field.issuer.label"
            yield "subject", "authority.field.subject.label"
            yield target.Name, target.LabelId
            for definition in commands do
                for input in definition.Inputs do
                    yield input.Name, input.LabelId
        ]
        |> List.distinct
        |> List.groupBy fst
        |> List.map (fun (_, values) ->
            match values with
            | [ name, identity ] -> name, AuthoredMessages.literal identity, identity
            | _ -> invalidOp "Protocol field presentation identities disagree.")

type FieldPresentationDefinition =
    {
        Name: BusinessField
        LabelId: string
        MeaningId: string
        Scalar: ScalarRule
        AllowsAbsence: bool
    }

type PresentationDefinitions =
    {
        Fields: FieldPresentationDefinition list
        Commands: CommandDefinition list
        AuthorityTarget: AuthorityInputDefinition
        AuthorityCommands: AuthorityOperationDefinition list
    }

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

    let private fields definitions (writer: Utf8JsonWriter) =
        writer.WritePropertyName("fields")
        writer.WriteStartArray()

        for field in definitions.Fields do
            writer.WriteStartObject()
            writer.WriteString("name", BusinessField.token field.Name)
            writer.WriteString("label", AuthoredMessages.literal field.LabelId)
            writer.WriteString("labelId", field.LabelId)
            writer.WriteString("meaning", (AuthoredMessages.literal field.MeaningId))
            writer.WriteString("meaningId", field.MeaningId)
            writer.WriteBoolean("allowsAbsence", field.AllowsAbsence)
            writer.WritePropertyName("scalar")
            writer.WriteStartObject()
            scalar writer field.Scalar
            writer.WriteEndObject()
            writer.WriteEndObject()

        writer.WriteEndArray()

    let private commands definitions (writer: Utf8JsonWriter) =
        writer.WritePropertyName("commands")
        writer.WriteStartArray()

        for command in definitions.Commands do
            writer.WriteStartObject()
            writer.WriteString("kind", CaseActKinds.token command.Kind)
            writer.WriteString("label", (AuthoredMessages.literal command.LabelId))
            writer.WriteString("meaning", (AuthoredMessages.literal command.MeaningId))
            writer.WriteString("labelId", command.LabelId)

            writer.WriteString("meaningId", command.MeaningId)

            writer.WriteString("confirmationId", command.ConfirmationId)
            CommandInputRendering.write writer command.Inputs
            writer.WriteEndObject()

        writer.WriteEndArray()

    let private inputFields (writer: Utf8JsonWriter) =
        writer.WritePropertyName("inputFields")
        writer.WriteStartArray()
        writer.WriteStartObject()
        writer.WriteString("name", "reason")
        writer.WriteString("labelId", "field.reason.label")
        writer.WriteString("meaningId", "field.reason.meaning")
        writer.WriteString("label", (AuthoredMessages.literal "field.reason.label"))
        writer.WriteString("meaning", (AuthoredMessages.literal "field.reason.meaning"))
        writer.WritePropertyName("scalar")
        writer.WriteStartObject()
        scalar writer (FieldDefinitions.scalar InputTarget.Reason)
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
        writer.WriteString("label", (AuthoredMessages.literal input.LabelId))
        writer.WriteString("labelId", input.LabelId)
        writer.WriteString("primitive", authorityPrimitive input.Primitive)
        writer.WritePropertyName("path")
        writer.WriteStartArray()

        for part in prefix @ [ input.Name ] do
            writer.WriteStringValue(part: string)

        writer.WriteEndArray()
        writer.WriteEndObject()

    let private authorityFields definitions (writer: Utf8JsonWriter) =
        writer.WritePropertyName("authorityFields")
        writer.WriteStartArray()

        for name, label, labelId in
            ProtocolFieldPresentation.authorityFields
                definitions.AuthorityTarget
                definitions.AuthorityCommands do
            writer.WriteStartObject()
            writer.WriteString("name", name)
            writer.WriteString("label", label)
            writer.WriteString("labelId", labelId)
            writer.WriteEndObject()

        writer.WriteEndArray()

    let private authority definitions (writer: Utf8JsonWriter) =
        writer.WritePropertyName("authorityTarget")
        authorityInput writer [] definitions.AuthorityTarget
        writer.WritePropertyName("authorityCommands")
        writer.WriteStartArray()

        for definition in definitions.AuthorityCommands do
            writer.WriteStartObject()
            writer.WriteString("kind", AuthorityKinds.token definition.Kind)
            writer.WriteString("label", (AuthoredMessages.literal definition.LabelId))
            writer.WriteString("meaning", (AuthoredMessages.literal definition.MeaningId))

            writer.WriteString("labelId", definition.LabelId)

            writer.WriteString("meaningId", definition.MeaningId)

            writer.WriteString("confirmationId", definition.ConfirmationId)
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

    let defaults =
        {
            Fields =
                FieldDefinitions.all
                |> List.map (fun field ->
                    {
                        Name = field.Field
                        LabelId = FieldTable.labelId field.Field
                        MeaningId = FieldTable.meaningId field.Field
                        Scalar = field.Scalar
                        AllowsAbsence = (FieldTable.forField field.Field).Optional
                    })
            Commands = CommandDefinitions.all
            AuthorityTarget = AuthorityOperationDefinitions.target
            AuthorityCommands = AuthorityOperationDefinitions.all
        }

    let renderFrom definitions =
        ContractJson.contract (fun writer ->
            writer.WriteStartObject()
            fields definitions writer
            commands definitions writer
            inputFields writer
            authority definitions writer
            authorityFields definitions writer
            writer.WritePropertyName("statuses")
            writer.WriteStartArray()
            CaseStatuses.all |> List.iter (CaseStatuses.token >> writer.WriteStringValue)
            writer.WriteEndArray()
            diagnostics writer "rejectionDiagnostics" Refusals.definitions
            diagnostics writer "faultDiagnostics" CoreFaults.definitions
            writer.WritePropertyName("roles")
            writer.WriteStartArray()

            for role in RoleTokens.all do
                writer.WriteStartObject()
                writer.WriteString("role", RoleTokens.token role)
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

    let render () = renderFrom defaults
