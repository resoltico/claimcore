namespace ClaimCore.ContractGeneration

open System.Text.Json
open ClaimCore.Application
open ClaimCore.Contracts

/// Build-only English projection; no runtime locale or discovery surface.
module DefaultPresentation =
    let private literal (value: string) =
        System.Text.RegularExpressions.Regex.Replace(value.Replace("'", "''"), "[{}]+", "'$0'")

    let private rejection identifier =
        RejectionPresentation.template identifier
        |> List.map (function
            | DiagnosticTextPart.Literal value -> literal value
            | DiagnosticTextPart.Hole hole -> "{" + RejectionPresentation.holeName hole + "}")
        |> String.concat ""

    let bytes () =
        [
            yield!
                RejectionDiagnosticIds.all
                |> List.map (fun (reason, id) -> "diagnostic." + id, rejection reason)
            yield!
                CoreFaults.all
                |> List.map (fun (reason, id) ->
                    "diagnostic." + id, CoreFaultPresentation.render reason |> literal)
            yield!
                RecoveryRejections.all
                |> List.map (fun (reason, id) ->
                    "diagnostic." + id, RecoveryRejectionPresentation.render reason |> literal)
            yield!
                WebHostFailures.all
                |> List.map (fun reason ->
                    "diagnostic." + WebHostFailures.token reason,
                    WebHostFailures.render reason |> literal)
        ]
        |> Map.ofList
        |> JsonSerializer.SerializeToUtf8Bytes
