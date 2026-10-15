namespace ClaimCore.Contracts

open System.Text.Json
open ClaimCore.Application

module DefaultPresentation =
    let render () =
        let values =
            [
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
