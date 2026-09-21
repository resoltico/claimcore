namespace ClaimCore.Contracts

open ClaimCore.Application

module RejectionDiagnosticSchemas =
    let private property name schema = Schema.property name schema true
    let private text value = Schema.constant (TextConstant value)

    let private number value =
        Schema.constant (IntegerConstant(int64 value))

    let private parameterSchema (parameter: DiagnosticParameterDefinition) =
        property
            parameter.Name
            (Schema.integer (Some(int64 parameter.Minimum)) (Some(int64 parameter.Maximum)))

    let value =
        RejectionDiagnosticIds.definitions
        // Equal parameter shapes share one variant; IDs remain disjoint and exact.
        // This preserves strict validation without duplicating empty-object validators.
        |> List.groupBy (fun definition -> definition.Parameters)
        |> List.map (fun (parameters, definitions) ->
            Schema.objectOf
                false
                [
                    property
                        "id"
                        (definitions
                         |> List.map (fun item -> TextConstant item.Id)
                         |> Schema.enumeration)
                    property
                        "parameters"
                        (parameters |> List.map parameterSchema |> Schema.objectOf false)
                ])
        |> Schema.oneOf

    let private parameterDefinition parameters =
        parameters
        |> List.map (fun parameter ->
            Schema.objectOf
                false
                [
                    property "name" (text parameter.Name)
                    property "minimum" (number parameter.Minimum)
                    property "maximum" (number parameter.Maximum)
                ])
        |> Schema.tuple

    let private definitionWith parameters (diagnostic: RejectionDiagnosticDefinition) =
        Schema.objectOf
            false
            [ property "id" (text diagnostic.Id); property "parameters" parameters ]

    let definition (diagnostic: RejectionDiagnosticDefinition) =
        definitionWith (parameterDefinition diagnostic.Parameters) diagnostic

    /// Share repeated parameter metadata in Web schemas without changing catalogue admission.
    let sharedCatalogue (diagnostics: RejectionDiagnosticDefinition list) =
        let shapes = diagnostics |> List.map _.Parameters |> List.distinct

        let name index =
            "RejectionParameterDefinition" + string index

        let definitions =
            shapes |> List.mapi (fun index shape -> name index, parameterDefinition shape)

        let catalogue =
            diagnostics
            |> List.map (fun diagnostic ->
                let index = shapes |> List.findIndex ((=) diagnostic.Parameters)
                definitionWith (Schema.reference (name index)) diagnostic)
            |> Schema.tuple

        catalogue, definitions
