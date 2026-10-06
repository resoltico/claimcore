namespace ClaimCore.ContractGeneration

open System.Text.Json
open ClaimCore.Application
open ClaimCore.Contracts

/// Build-only English projection; no runtime locale or discovery surface.
module DefaultPresentation =
    let literal (value: string) = Map.ofList [ "literal", value ]

    let private rejection identifier =
        RejectionPresentation.template identifier
        |> List.map (function
            | DiagnosticTextPart.Literal value -> literal value
            | DiagnosticTextPart.Hole hole ->
                Map.ofList [ "hole", RejectionPresentation.holeName hole ])

    let bytes () =
        [
            yield!
                RejectionDiagnosticIds.all
                |> List.map (fun (reason, id) -> "diagnostic." + id, rejection reason)
            yield!
                CoreFaults.all
                |> List.map (fun (reason, id) ->
                    "diagnostic." + id, [ literal (CoreFaultPresentation.render reason) ])
            yield!
                RecoveryRejections.all
                |> List.map (fun (reason, id) ->
                    "diagnostic." + id, [ literal (RecoveryRejectionPresentation.render reason) ])
            yield!
                WebHostFailures.all
                |> List.map (fun reason ->
                    "diagnostic." + WebHostFailures.token reason,
                    [ literal (WebHostFailures.render reason) ])
        ]
        |> Map.ofList
        |> JsonSerializer.SerializeToUtf8Bytes
