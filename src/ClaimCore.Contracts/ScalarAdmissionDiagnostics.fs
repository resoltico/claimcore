namespace ClaimCore.Contracts

open ClaimCore.Application
open ClaimCore.Domain

/// The local schema boundary can diagnose scalar syntax only, never state, dates or authority.
module ScalarAdmissionDiagnostics =
    let private identifiers =
        set
            [
                RejectionDiagnosticId.TextRequired
                RejectionDiagnosticId.MalformedUnicode
                RejectionDiagnosticId.SurroundingWhitespace
                RejectionDiagnosticId.TextTooShort
                RejectionDiagnosticId.TextTooLong
                RejectionDiagnosticId.ControlCharacters
                RejectionDiagnosticId.CalendarDateRequired
                RejectionDiagnosticId.DecimalFormat
                RejectionDiagnosticId.CurrencyFormat
            ]

    let supported value =
        Set.contains (RejectionDiagnostics.identifier value) identifiers

    let definitions =
        let tokens = identifiers |> Set.map RejectionDiagnosticIds.token

        RejectionDiagnosticIds.definitions
        |> List.filter (fun value -> Set.contains value.Id tokens)

    let internal describe path violation =
        let target =
            path
            |> List.tryLast
            |> Option.bind (fun name ->
                InputTargets.all |> List.tryFind (snd >> (=) name) |> Option.map fst)
            |> Option.defaultValue InputTarget.Fields

        let diagnostic =
            Rejection.Domain(DomainError.InvalidInput(target, violation))
            |> RejectionDiagnostics.describe

        if supported diagnostic then Some diagnostic else None
