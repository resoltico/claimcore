namespace ClaimCore.Cli

open System
open System.Text.Json
open ClaimCore.Contracts

module CliRemoteInvocation =
    let private failure reason path =
        Error(ProtocolFailure.create reason (ProtocolLocation.fromPath path))

    let private validateInput (model: ContractModel) (endpoint: CliEndpoint) input =
        let definitions =
            model.WebEndpoints
            |> List.tryHead
            |> Option.map (WebSchemas.responseDocument model >> _.Definitions)
            |> Option.defaultValue []

        let schema =
            {
                Identifier = "https://claimcore.local/contracts/cli-v4.input"
                Title = "ClaimCore CLI input"
                Root = endpoint.Input
                Definitions = definitions
            }

        if SchemaValueValidation.verify schema input then
            Ok input
        else
            failure ProtocolProblem.InvalidToken "/input"

    let private timeout endpoint frame identifier input =
        match StrictJson.optionalProperty "timeoutMs" frame with
        | None -> Ok(identifier, input, None)
        | Some duration ->
            StrictJson.integerAt "/timeoutMs" 1 60000 duration
            |> Result.bind (fun value ->
                if endpoint.Cancellable then
                    Ok(identifier, input, Some value)
                else
                    failure ProtocolProblem.TimeoutForbidden "/timeoutMs")

    let decode (root: JsonElement) =
        StrictJson.allowedProperties
            ""
            [ "protocolVersion"; "endpoint"; "input" ]
            [ "protocolVersion"; "endpoint"; "input"; "timeoutMs" ]
            root
        |> Result.bind (fun frame ->
            StrictJson.requiredProperty "" "protocolVersion" frame
            |> Result.bind (StrictJson.integerAt "/protocolVersion" 4 4)
            |> Result.bind (fun _ ->
                StrictJson.requiredProperty "" "endpoint" frame
                |> Result.bind (StrictJson.stringAt "/endpoint")
                |> Result.bind (fun identifier ->
                    let model = ContractProjection.current ()

                    match
                        model.CliEndpoints
                        |> List.tryFind (fun item -> item.Identifier = identifier)
                    with
                    | None -> failure ProtocolProblem.UnknownEndpoint "/endpoint"
                    | Some endpoint ->
                        StrictJson.requiredProperty "" "input" frame
                        |> Result.bind (validateInput model endpoint)
                        |> Result.bind (timeout endpoint frame identifier))))
