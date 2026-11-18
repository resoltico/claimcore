namespace ClaimCore.Domain

/// Search projection policy; independent of display order and prominence.
module CaseSearch =
    let private searchable =
        function
        | BusinessField.IncidentDescription
        | BusinessField.PolicyNumber
        | BusinessField.InsurerClaimReference
        | BusinessField.CaseReference
        | BusinessField.HandlingSummary -> true
        // Search is opt-in; a newly declared optional fact is not automatically indexed.
        | _ -> false

    let fields =
        BusinessField.declarations |> List.map _.Field |> List.filter searchable

    let text parties values =
        (fields |> List.choose (fun field -> CaseFields.value field values))
        @ (parties |> List.collect PartyContent.searchable)
        |> List.map TextSearch.normalize
        |> String.concat "\u001f"
