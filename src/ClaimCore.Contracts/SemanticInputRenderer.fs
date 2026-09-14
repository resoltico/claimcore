namespace ClaimCore.Contracts

open System.Text.Json
open ClaimCore.Domain

/// Renders Domain's one input descriptor into the semantic contract without flattening correction
/// groups into transport-specific optional fields.
module internal SemanticInputRenderer =
    let private field (writer: Utf8JsonWriter) (input: FieldInputDefinition) =
        writer.WriteStartObject()
        writer.WriteString("fieldName", input.FieldName)

        match input.Prefill with
        | PrefillSource.Blank -> writer.WriteString("prefill", "BLANK")
        | PrefillSource.CurrentField fieldName ->
            writer.WriteString("prefill", "CURRENT_FIELD")
            writer.WriteString("currentField", fieldName)

        writer.WriteEndObject()

    let private action =
        function
        | CorrectionGroupAction.Keep -> "KEEP"
        | CorrectionGroupAction.Replace -> "REPLACE"
        | CorrectionGroupAction.Clear -> "CLEAR"

    let write (writer: Utf8JsonWriter) input =
        writer.WritePropertyName("inputs")
        writer.WriteStartObject()

        match input with
        | CommandInputShape.Fields fields ->
            writer.WriteString("kind", "FIELDS")
            writer.WritePropertyName("fields")
            writer.WriteStartArray()
            fields |> List.iter (field writer)
            writer.WriteEndArray()
        | CommandInputShape.CorrectionGroups groups ->
            writer.WriteString("kind", "CORRECTION_GROUPS")
            writer.WritePropertyName("groups")
            writer.WriteStartArray()

            groups
            |> List.iter (fun group ->
                writer.WriteStartObject()
                writer.WriteString("name", group.Name)
                writer.WriteString("label", group.Label)
                writer.WriteString("meaning", group.Meaning)
                writer.WritePropertyName("actions")
                writer.WriteStartArray()
                group.Actions |> List.iter (action >> writer.WriteStringValue)
                writer.WriteEndArray()
                writer.WritePropertyName("replaceFields")
                writer.WriteStartArray()
                group.ReplaceFields |> List.iter (field writer)
                writer.WriteEndArray()
                writer.WriteEndObject())

            writer.WriteEndArray()

        writer.WriteEndObject()
