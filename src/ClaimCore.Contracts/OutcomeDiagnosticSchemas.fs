namespace ClaimCore.Contracts

open ClaimCore.Application

/// Correlates machine identity and guidance; English copy is not a discriminator.
module OutcomeDiagnosticSchemas =
    let private property name schema = Schema.property name schema true
    let private token value = Schema.constant (TextConstant value)

    let private variant ((code, action), identifiers) =
        let diagnostic =
            Schema.objectOf
                false
                [
                    property "id" (identifiers |> List.map TextConstant |> Schema.enumeration)
                    property "parameters" (Schema.objectOf false [])
                ]

        Schema.objectOf
            false
            [
                property "code" (token code)
                property "diagnostic" diagnostic
                property "message" (Schema.string None None (Some 1) None)
                property "recommendedAction" (token action)
            ]

    let private variants values =
        values
        |> List.groupBy (fun (code, action, _) -> code, action)
        |> List.map (fun (key, items) -> key, items |> List.map (fun (_, _, id) -> id))
        |> List.map variant
        |> Schema.oneOf

    let fault =
        CoreFaults.all
        |> List.map (fun (reason, id) ->
            WireTokens.faultCode reason.Code, WireTokens.action reason.Action, id)
        |> variants

    let recoveryRejection =
        RecoveryRejections.all
        |> List.map (fun (reason, id) ->
            WireTokens.recoveryRejectionCode reason.Code, WireTokens.action reason.Action, id)
        |> variants

    let localFault =
        CliLocalFaults.all
        |> List.map (fun reason ->
            WireTokens.faultCode (CliLocalFaults.code reason),
            WireTokens.action RecommendedAction.StopAndInvestigate,
            CliLocalFaults.token reason)
        |> variants
