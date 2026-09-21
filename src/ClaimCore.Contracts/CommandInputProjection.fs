namespace ClaimCore.Contracts

open ClaimCore.Domain

/// Projects the one Domain command-input descriptor into both discovery metadata and exact request
/// schemas. Grouped correction input never becomes an optional flat scalar bag.
module internal CommandInputProjection =
    let private text value = Schema.constant (TextConstant value)

    let private exactArray (values: Schema list) = Schema.tuple values

    let field (input: FieldInputDefinition) =
        match input.Prefill with
        | PrefillSource.Blank ->
            Schema.objectOf
                false
                [
                    Schema.property "fieldName" (text input.FieldName) true
                    Schema.property "prefill" (text "BLANK") true
                ]
        | PrefillSource.CurrentField current ->
            Schema.objectOf
                false
                [
                    Schema.property "fieldName" (text input.FieldName) true
                    Schema.property "prefill" (text "CURRENT_FIELD") true
                    Schema.property "currentField" (text current) true
                ]

    let private action action =
        match action with
        | CorrectionGroupAction.Keep -> "KEEP"
        | CorrectionGroupAction.Replace -> "REPLACE"
        | CorrectionGroupAction.Clear -> "CLEAR"

    let private correctionGroupDefinition (group: CorrectionGroupDefinition) =
        Schema.objectOf
            false
            [
                Schema.property "name" (text group.Name) true
                Schema.property "label" (Schema.string None None (Some 1) (Some 4096)) true
                Schema.property "meaning" (Schema.string None None (Some 1) (Some 4096)) true
                Schema.property
                    "actions"
                    (group.Actions |> List.map (action >> text) |> exactArray)
                    true
                Schema.property
                    "replaceFields"
                    (group.ReplaceFields |> List.map field |> exactArray)
                    true
            ]

    let definition =
        function
        | CommandInputShape.Fields fields ->
            Schema.objectOf
                false
                [
                    Schema.property "kind" (text "FIELDS") true
                    Schema.property "fields" (fields |> List.map field |> exactArray) true
                ]
        | CommandInputShape.CorrectionGroups groups ->
            Schema.objectOf
                false
                [
                    Schema.property "kind" (text "CORRECTION_GROUPS") true
                    Schema.property
                        "groups"
                        (groups |> List.map correctionGroupDefinition |> exactArray)
                        true
                ]

    let private values fieldByName fields =
        fields
        |> List.map (fun input ->
            let field = Map.find input.FieldName fieldByName
            Schema.property input.FieldName (ScalarSchemas.scalar field.Scalar) true)
        |> Schema.objectOf false

    let private correctionGroupValue fieldByName (group: CorrectionGroupDefinition) =
        group.Actions
        |> List.map (fun action ->
            match action with
            | CorrectionGroupAction.Keep ->
                Schema.objectOf false [ Schema.property "mode" (text "KEEP") true ]
            | CorrectionGroupAction.Clear ->
                Schema.objectOf false [ Schema.property "mode" (text "CLEAR") true ]
            | CorrectionGroupAction.Replace ->
                Schema.objectOf
                    false
                    [
                        Schema.property "mode" (text "REPLACE") true
                        Schema.property "values" (values fieldByName group.ReplaceFields) true
                    ])
        |> Schema.oneOf

    let payload (fields: FieldDefinition list) (definition: CommandDefinition) =
        let fieldByName = fields |> List.map (fun field -> field.Name, field) |> Map.ofList
        let kind = Schema.property "kind" (text (CommandKinds.token definition.Kind)) true

        match definition.Inputs with
        | CommandInputShape.Fields inputs ->
            Schema.objectOf
                false
                [ kind; Schema.property "values" (values fieldByName inputs) true ]
        | CommandInputShape.CorrectionGroups groups ->
            let groupedValues =
                groups
                |> List.map (fun group ->
                    Schema.property group.Name (correctionGroupValue fieldByName group) true)
                |> Schema.objectOf false

            Schema.objectOf false [ kind; Schema.property "groups" groupedValues true ]
