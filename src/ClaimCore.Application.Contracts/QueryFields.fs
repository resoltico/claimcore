namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

/// Facts selected by the machine summary query, independent of human table layout.
module CaseSummaryFields =
    let declarations =
        [
            BusinessField.ClaimantName
            BusinessField.InsurerName
            BusinessField.ClaimedAmount
            BusinessField.ClaimedCurrency
            BusinessField.PayableAmount
            BusinessField.PayableCurrency
        ]
        |> List.map BusinessField.describe

    let values declarations fields =
        declarations
        |> List.map (fun declaration ->
            declaration.Field, CaseFields.typedValue declaration.Field fields)
        |> Map.ofList
