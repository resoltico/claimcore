namespace ClaimCore.Contracts

open ClaimCore.ClientProtocol

open System.Text.Json
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.Domain

module DefaultPresentation =
    let private operationMessages =
        [
            for definition in CommandDefinitions.all do
                yield
                    "command." + CaseActKinds.token definition.Kind + ".label",
                    [ Map.ofList [ "literal", definition.Label ] ]

                yield
                    "command." + CaseActKinds.token definition.Kind + ".meaning",
                    [ Map.ofList [ "literal", definition.Meaning ] ]
            for definition in AuthorityOperationDefinitions.all do
                yield
                    "authority." + RecordedAuthorityActs.token definition.Kind + ".label",
                    [ Map.ofList [ "literal", definition.Label ] ]

                yield
                    "authority." + RecordedAuthorityActs.token definition.Kind + ".meaning",
                    [ Map.ofList [ "literal", definition.Meaning ] ]
        ]

    let render () =
        let values =
            [
                yield! operationMessages
                for _, label, labelId in ProtocolFieldPresentation.authorityFields do
                    yield labelId, [ Map.ofList [ "literal", label ] ]
                yield!
                    RejectionDiagnosticIds.all
                    |> List.map (fun (reason, id) ->
                        "diagnostic." + id,
                        (RejectionPresentation.template reason
                         |> List.map (function
                             | DiagnosticTextPart.Literal value -> Map.ofList [ "literal", value ]
                             | DiagnosticTextPart.Hole hole ->
                                 Map.ofList [ "hole", RejectionPresentation.holeName hole ])))
                yield!
                    CoreFaults.all
                    |> List.map (fun (reason, id) ->
                        "diagnostic." + id,
                        [ Map.ofList [ "literal", CoreFaultPresentation.render reason ] ])
                yield!
                    HostFailures.all
                    |> List.map (fun reason ->
                        "diagnostic." + HostFailures.diagnosticId reason,
                        [ Map.ofList [ "literal", HostFailures.render reason ] ])
            ]
            |> Map.ofList

        ContractJson.contract (fun writer -> JsonSerializer.Serialize(writer, values))
