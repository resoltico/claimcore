#if CONTRACT_GENERATION
namespace ClaimCore.ContractGeneration.Presentation

#else
namespace ClaimCore.Hosting.Presentation
#endif

open ClaimCore.Contract

open System.Globalization
open ClaimCore.Application
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type internal DiagnosticTextPart =
    | Literal of string
    | Hole of DiagnosticArgument

/// Outward rendering binds Catalog templates to Application-owned safe parameters.
module internal RejectionPresentation =
    let private integer (value: int) =
        value.ToString(CultureInfo.InvariantCulture)

    let template identifier =
        MessageText.template ("diagnostic." + RejectionDiagnosticIds.token identifier)
        |> List.map (function
            | MessagePart.Literal value -> DiagnosticTextPart.Literal value
            | MessagePart.Hole name ->
                DiagnosticArguments.tryParse name
                |> Option.map DiagnosticTextPart.Hole
                |> Option.defaultWith (fun () -> invalidOp "Unknown diagnostic argument."))

    let holeName = DiagnosticArguments.token

    let private holeValue parameters hole =
        match hole, parameters with
        | DiagnosticArgument.MaximumCharacters, DiagnosticParameters.MaximumCharacters value
        | DiagnosticArgument.MaximumPageSize, DiagnosticParameters.MaximumPageSize value ->
            integer value
        | DiagnosticArgument.MaximumIntegerDigits, DiagnosticParameters.DecimalDigits(value, _) ->
            integer value
        | DiagnosticArgument.MaximumFractionalDigits, DiagnosticParameters.DecimalDigits(_, value) ->
            integer value
        | _ -> invalidOp "Diagnostic template and typed parameters disagree."

    let render (rejection: Refusal) =
        let diagnostic = RejectionDiagnostics.describe rejection

        template (RejectionDiagnostics.identifier diagnostic)
        |> List.map (function
            | DiagnosticTextPart.Literal value -> value
            | DiagnosticTextPart.Hole hole ->
                holeValue (RejectionDiagnostics.parameters diagnostic) hole)
        |> String.concat ""
