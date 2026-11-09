namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

/// Search projection policy; independent of display order and prominence.
module CaseSearch =
    let private searchable =
        function
        | BusinessField.IncidentDescription
        | BusinessField.PolicyNumber
        | BusinessField.InsurerClaimReference
        | BusinessField.ClaimantName
        | BusinessField.InsurerName
        | BusinessField.CaseReference
        | BusinessField.HandlingSummary -> true
        | BusinessField.IncidentDate
        | BusinessField.IncidentNotificationDate
        | BusinessField.IncidentCountry
        | BusinessField.ClaimedAmount
        | BusinessField.ClaimedCurrency
        | BusinessField.PaymentDecisionDate
        | BusinessField.PayableAmount
        | BusinessField.PayableCurrency
        | BusinessField.PaymentDate
        | BusinessField.PaidAmount
        | BusinessField.PaidCurrency
        | BusinessField.Status
        | BusinessField.ResponsibleActor -> false

    let fields =
        BusinessField.declarations |> List.map _.Field |> List.filter searchable

    let text values =
        fields
        |> List.choose (fun field -> CaseFields.value field values)
        |> List.map TextSearch.normalize
        |> String.concat "\u001f"

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
