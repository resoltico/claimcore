namespace ClaimCore.Application

open ClaimCore.Contract

type RejectionDiagnostic =
    private
        {
            Identifier: RejectionDiagnosticId
            Parameters: DiagnosticParameters
        }

module RejectionDiagnostics =
    let describe reason =
        let code = (Refusals.row reason).Code
        let identifier = RejectionDiagnosticIds.all |> List.find (snd >> (=) code) |> fst

        let parameters =
            match reason with
            | Refusal.InvalidInput(_, Violation.TooLong maximum) ->
                DiagnosticParameters.MaximumCharacters maximum
            | Refusal.InvalidInput(_, Violation.DecimalFormat(integer, fraction)) ->
                DiagnosticParameters.DecimalDigits(integer, fraction)
            | Refusal.InvalidInput(_, Violation.PageLimitRange maximum) ->
                DiagnosticParameters.MaximumPageSize maximum
            | _ -> DiagnosticParameters.None

        {
            Identifier = identifier
            Parameters = parameters
        }

    let identifier diagnostic = diagnostic.Identifier
    let parameters diagnostic = diagnostic.Parameters

    let values diagnostic =
        match diagnostic.Parameters with
        | DiagnosticParameters.None -> []
        | DiagnosticParameters.MaximumCharacters value -> [ "maximumCharacters", value ]
        | DiagnosticParameters.DecimalDigits(integer, fraction) ->
            [ "maximumIntegerDigits", integer; "maximumFractionalDigits", fraction ]
        | DiagnosticParameters.MaximumPageSize value -> [ "maximumPageSize", value ]
