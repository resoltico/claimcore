namespace ClaimCore.Contracts

/// Correlates host diagnostic identity, HTTP status and execution knowledge.
module WebHostFailureSchema =
    let schema =
        WebHostFailures.all
        |> List.groupBy (fun reason ->
            WebHostFailures.code reason,
            WebHostFailures.status reason,
            WebHostFailures.phase reason)
        |> List.map (fun ((code, status, phase), reasons) ->
            WireSchema.objectOf
                [
                    WireSchema.property "kind" (WireSchema.token "HOST_FAILURE")
                    WireSchema.property "code" (WireSchema.token code)
                    WireSchema.property "status" (WireSchema.number status)
                    WireSchema.property "message" WireSchema.text
                    WireSchema.property
                        "diagnostic"
                        (WireSchema.objectOf
                            [
                                WireSchema.property
                                    "id"
                                    (reasons
                                     |> List.map WebHostFailures.token
                                     |> WireSchema.enumeration)
                                WireSchema.property "parameters" (WireSchema.objectOf [])
                            ])
                    WireSchema.property
                        "executionPhase"
                        (phase
                         |> Option.map WireSchema.token
                         |> Option.defaultValue Schema.nullValue)
                ])
        |> Schema.oneOf
