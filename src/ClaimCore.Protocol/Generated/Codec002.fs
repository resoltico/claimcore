// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal FieldDescriptorScalarTextJson =
    let private properties =
        [
            "kind"
            "minimumCharacters"
            "maximumCharacters"
            "requiresNonBlank"
            "rejectsSurroundingWhitespace"
            "rejectsControlCharacters"
            "requiresWellFormedUnicode"
        ]

    let read (value: JsonElement) : FieldDescriptorScalarText =
        JsonRead.objectValue properties value

        {
            Kind = JsonRead.required "kind" (ProtocolScalar29.read) value
            MinimumCharacters = JsonRead.required "minimumCharacters" (ProtocolScalar30.read) value
            MaximumCharacters = JsonRead.required "maximumCharacters" (ProtocolScalar30.read) value
            RequiresNonBlank = JsonRead.required "requiresNonBlank" (ProtocolScalar9.read) value
            RejectsSurroundingWhitespace =
                JsonRead.required "rejectsSurroundingWhitespace" (ProtocolScalar9.read) value
            RejectsControlCharacters =
                JsonRead.required "rejectsControlCharacters" (ProtocolScalar9.read) value
            RequiresWellFormedUnicode =
                JsonRead.required "requiresWellFormedUnicode" (ProtocolScalar9.read) value
        }

    let write (writer: Utf8JsonWriter) (value: FieldDescriptorScalarText) =
        writer.WriteStartObject()
        JsonWrite.property "kind" (ProtocolScalar29.write) writer value.Kind

        JsonWrite.property
            "minimumCharacters"
            (ProtocolScalar30.write)
            writer
            value.MinimumCharacters

        JsonWrite.property
            "maximumCharacters"
            (ProtocolScalar30.write)
            writer
            value.MaximumCharacters

        JsonWrite.property "requiresNonBlank" (ProtocolScalar9.write) writer value.RequiresNonBlank

        JsonWrite.property
            "rejectsSurroundingWhitespace"
            (ProtocolScalar9.write)
            writer
            value.RejectsSurroundingWhitespace

        JsonWrite.property
            "rejectsControlCharacters"
            (ProtocolScalar9.write)
            writer
            value.RejectsControlCharacters

        JsonWrite.property
            "requiresWellFormedUnicode"
            (ProtocolScalar9.write)
            writer
            value.RequiresWellFormedUnicode

        writer.WriteEndObject()

module internal FieldDescriptorScalarCalendarDateJson =
    let private properties = [ "kind"; "exactFormat"; "minimum"; "maximum" ]

    let read (value: JsonElement) : FieldDescriptorScalarCalendarDate =
        JsonRead.objectValue properties value

        {
            Kind = JsonRead.required "kind" (ProtocolScalar31.read) value
            ExactFormat = JsonRead.required "exactFormat" (ProtocolScalar8.read) value
            Minimum = JsonRead.required "minimum" (ProtocolScalar0.read) value
            Maximum = JsonRead.required "maximum" (ProtocolScalar0.read) value
        }

    let write (writer: Utf8JsonWriter) (value: FieldDescriptorScalarCalendarDate) =
        writer.WriteStartObject()
        JsonWrite.property "kind" (ProtocolScalar31.write) writer value.Kind
        JsonWrite.property "exactFormat" (ProtocolScalar8.write) writer value.ExactFormat
        JsonWrite.property "minimum" (ProtocolScalar0.write) writer value.Minimum
        JsonWrite.property "maximum" (ProtocolScalar0.write) writer value.Maximum
        writer.WriteEndObject()

module internal FieldDescriptorScalarAmountJson =
    let private properties =
        [ "kind"; "grammar"; "maximumIntegerDigits"; "maximumFractionalDigits" ]

    let read (value: JsonElement) : FieldDescriptorScalarAmount =
        JsonRead.objectValue properties value

        {
            Kind = JsonRead.required "kind" (ProtocolScalar32.read) value
            Grammar = JsonRead.required "grammar" (ProtocolScalar8.read) value
            MaximumIntegerDigits =
                JsonRead.required "maximumIntegerDigits" (ProtocolScalar30.read) value
            MaximumFractionalDigits =
                JsonRead.required "maximumFractionalDigits" (ProtocolScalar30.read) value
        }

    let write (writer: Utf8JsonWriter) (value: FieldDescriptorScalarAmount) =
        writer.WriteStartObject()
        JsonWrite.property "kind" (ProtocolScalar32.write) writer value.Kind
        JsonWrite.property "grammar" (ProtocolScalar8.write) writer value.Grammar

        JsonWrite.property
            "maximumIntegerDigits"
            (ProtocolScalar30.write)
            writer
            value.MaximumIntegerDigits

        JsonWrite.property
            "maximumFractionalDigits"
            (ProtocolScalar30.write)
            writer
            value.MaximumFractionalDigits

        writer.WriteEndObject()

module internal FieldDescriptorScalarCurrencyJson =
    let private properties = [ "kind"; "grammar"; "exactCharacters" ]

    let read (value: JsonElement) : FieldDescriptorScalarCurrency =
        JsonRead.objectValue properties value

        {
            Kind = JsonRead.required "kind" (ProtocolScalar33.read) value
            Grammar = JsonRead.required "grammar" (ProtocolScalar8.read) value
            ExactCharacters = JsonRead.required "exactCharacters" (ProtocolScalar30.read) value
        }

    let write (writer: Utf8JsonWriter) (value: FieldDescriptorScalarCurrency) =
        writer.WriteStartObject()
        JsonWrite.property "kind" (ProtocolScalar33.write) writer value.Kind
        JsonWrite.property "grammar" (ProtocolScalar8.write) writer value.Grammar
        JsonWrite.property "exactCharacters" (ProtocolScalar30.write) writer value.ExactCharacters
        writer.WriteEndObject()

module internal FieldDescriptorScalarCaseStatusJson =
    let private properties = [ "kind"; "allowedValues" ]

    let read (value: JsonElement) : FieldDescriptorScalarCaseStatus =
        JsonRead.objectValue properties value

        {
            Kind = JsonRead.required "kind" (ProtocolScalar34.read) value
            AllowedValues =
                JsonRead.required
                    "allowedValues"
                    (JsonRead.array None None (ProtocolScalar6.read))
                    value
        }

    let write (writer: Utf8JsonWriter) (value: FieldDescriptorScalarCaseStatus) =
        writer.WriteStartObject()
        JsonWrite.property "kind" (ProtocolScalar34.write) writer value.Kind

        JsonWrite.property
            "allowedValues"
            (JsonWrite.array (ProtocolScalar6.write))
            writer
            value.AllowedValues

        writer.WriteEndObject()

module internal FieldDescriptorScalarJson =
    let read (value: JsonElement) : FieldDescriptorScalar =
        match JsonRead.tag "kind" value with
        | "TEXT" -> FieldDescriptorScalar.Text((FieldDescriptorScalarTextJson.read) value)
        | "CALENDAR_DATE" ->
            FieldDescriptorScalar.CalendarDate((FieldDescriptorScalarCalendarDateJson.read) value)
        | "AMOUNT" -> FieldDescriptorScalar.Amount((FieldDescriptorScalarAmountJson.read) value)
        | "CURRENCY" ->
            FieldDescriptorScalar.Currency((FieldDescriptorScalarCurrencyJson.read) value)
        | "CASE_STATUS" ->
            FieldDescriptorScalar.CaseStatus((FieldDescriptorScalarCaseStatusJson.read) value)
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: FieldDescriptorScalar) =
        match value with
        | FieldDescriptorScalar.Text item -> (FieldDescriptorScalarTextJson.write) writer item
        | FieldDescriptorScalar.CalendarDate item ->
            (FieldDescriptorScalarCalendarDateJson.write) writer item
        | FieldDescriptorScalar.Amount item -> (FieldDescriptorScalarAmountJson.write) writer item
        | FieldDescriptorScalar.Currency item ->
            (FieldDescriptorScalarCurrencyJson.write) writer item
        | FieldDescriptorScalar.CaseStatus item ->
            (FieldDescriptorScalarCaseStatusJson.write) writer item

module internal FieldDescriptorJson =
    let private properties =
        [ "name"; "nativeName"; "label"; "meaning"; "allowsAbsence"; "scalar" ]

    let read (value: JsonElement) : FieldDescriptor =
        JsonRead.objectValue properties value

        {
            Name = JsonRead.required "name" (ProtocolScalar8.read) value
            NativeName = JsonRead.required "nativeName" (ProtocolScalar8.read) value
            Label = JsonRead.required "label" (ProtocolScalar8.read) value
            Meaning = JsonRead.required "meaning" (ProtocolScalar8.read) value
            AllowsAbsence = JsonRead.required "allowsAbsence" (ProtocolScalar9.read) value
            Scalar = JsonRead.required "scalar" (FieldDescriptorScalarJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: FieldDescriptor) =
        writer.WriteStartObject()
        JsonWrite.property "name" (ProtocolScalar8.write) writer value.Name
        JsonWrite.property "nativeName" (ProtocolScalar8.write) writer value.NativeName
        JsonWrite.property "label" (ProtocolScalar8.write) writer value.Label
        JsonWrite.property "meaning" (ProtocolScalar8.write) writer value.Meaning
        JsonWrite.property "allowsAbsence" (ProtocolScalar9.write) writer value.AllowsAbsence
        JsonWrite.property "scalar" (FieldDescriptorScalarJson.write) writer value.Scalar
        writer.WriteEndObject()

module internal SemanticDefinitionRulesItemJson =
    let private properties = [ "identifier"; "category"; "meaning" ]

    let read (value: JsonElement) : SemanticDefinitionRulesItem =
        JsonRead.objectValue properties value

        {
            Identifier = JsonRead.required "identifier" (ProtocolScalar8.read) value
            Category = JsonRead.required "category" (ProtocolScalar35.read) value
            Meaning = JsonRead.required "meaning" (ProtocolScalar8.read) value
        }

    let write (writer: Utf8JsonWriter) (value: SemanticDefinitionRulesItem) =
        writer.WriteStartObject()
        JsonWrite.property "identifier" (ProtocolScalar8.write) writer value.Identifier
        JsonWrite.property "category" (ProtocolScalar35.write) writer value.Category
        JsonWrite.property "meaning" (ProtocolScalar8.write) writer value.Meaning
        writer.WriteEndObject()
