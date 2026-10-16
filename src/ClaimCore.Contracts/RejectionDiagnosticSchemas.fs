namespace ClaimCore.Contracts

open ClaimCore.ClientProtocol

open ClaimCore.Application

module RejectionDiagnosticSchemas =
    let private property name schema = Schema.property name schema true

    let private parameterSchema (parameter: DiagnosticParameterDefinition) =
        property
            parameter.Name
            (Schema.integer (Some(int64 parameter.Minimum)) (Some(int64 parameter.Maximum)))

    let internal forDefinitions definitions =
        definitions
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

    let value = forDefinitions RejectionDiagnosticIds.definitions
