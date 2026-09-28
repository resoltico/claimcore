namespace ClaimCore.Contracts

open ClaimCore.Application

/// CLI-specific private-file inputs are the only deviation from the generated service API body.
/// Every routable identifier and its normal JSON input comes from the service catalog.
module internal CliEndpointCatalog =
    let private input identifier body =
        match identifier with
        | "recovery.export" -> Some EndpointInputs.recoveryExport
        | "recovery.importEnvelopePreview" -> Some EndpointInputs.importPreview
        | "recovery.importEnvelopeRetain" -> Some EndpointInputs.importRetain
        | _ ->
            match body with
            | Some(JsonBody value) -> Some value
            | _ -> None

    let private cancellable identifier =
        not (CliMutationCatalog.isMutation identifier)

    let all (semantic: SemanticCoreContract) =
        WebEndpointCatalog.all semantic
        |> List.choose (fun endpoint ->
            if
                Set.contains
                    endpoint.Identifier
                    (Set.ofList [ "session"; "session.logout"; "definition" ])
            then
                None
            else
                input endpoint.Identifier endpoint.Body
                |> Option.map (fun value ->
                    {
                        Identifier = endpoint.Identifier
                        Input = value
                        Cancellable = cancellable endpoint.Identifier
                    }))
