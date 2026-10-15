namespace ClaimCore.Contracts

open ClaimCore.Domain

/// Projects the one Domain command-input descriptor into both discovery metadata and exact request
/// schemas. Grouped correction input never becomes an optional flat scalar bag.
module internal CommandInputProjection =
    let private text value = Schema.constant (TextConstant value)

    let private values fields =
        fields
        |> List.map (fun input ->
            Schema.property
                input.FieldName
                (FieldDefinitions.scalar input.FieldName |> ScalarSchemas.scalar)
                true)
        |> Schema.objectOf false

    let private correctionGroupValue (group: CorrectionGroupDefinition) =
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
                        Schema.property "values" (values group.ReplaceFields) true
                    ])
        |> Schema.oneOf

    let payload (definition: CommandDefinition) =
        let kind = Schema.property "kind" (text (CaseActKinds.token definition.Kind)) true

        match definition.Inputs with
        | CommandInputShape.Fields inputs ->
            if inputs.IsEmpty then
                Schema.objectOf false [ kind ]
            else
                Schema.objectOf false [ kind; Schema.property "values" (values inputs) true ]
        | CommandInputShape.CorrectionGroups groups ->
            let groupedValues =
                groups
                |> List.map (fun group ->
                    Schema.property group.Name (correctionGroupValue group) true)
                |> Schema.objectOf false

            Schema.objectOf false [ kind; Schema.property "groups" groupedValues true ]
