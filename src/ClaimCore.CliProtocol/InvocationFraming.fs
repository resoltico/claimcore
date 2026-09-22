namespace ClaimCore.Cli

open System.Text.Json
open ClaimCore.Contracts

module InvocationFraming =
    let private failure reason path =
        Error(ProtocolFailure.create reason (ProtocolLocation.fromPath path))

    let private endpoint frame =
        StrictJson.requiredProperty "" "endpoint" frame
        |> Result.bind (StrictJson.stringAt "/endpoint")
        |> Result.bind (fun identifier ->
            Endpoint.tryParse identifier
            |> Option.map Ok
            |> Option.defaultValue (failure ProtocolProblem.UnknownEndpoint "/endpoint"))

    let private timeout endpoint frame =
        match StrictJson.optionalProperty "timeoutMs" frame with
        | None -> Ok None
        | Some value ->
            StrictJson.integerAt "/timeoutMs" 1 60000 value
            |> Result.bind (fun milliseconds ->
                let supported =
                    (ContractProjection.current ()).CliEndpoints
                    |> List.exists (fun item ->
                        item.Identifier = Endpoint.identifier endpoint && item.Cancellable)

                if supported then
                    Ok(Some milliseconds)
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
            |> Result.bind (StrictJson.integerAt "/protocolVersion" 3 3)
            |> Result.bind (fun _ ->
                endpoint frame
                |> Result.bind (fun selected ->
                    StrictJson.requiredProperty "" "input" frame
                    |> Result.bind (InvocationDecoder.decodeEndpoint selected)
                    |> Result.bind (fun input ->
                        timeout selected frame
                        |> Result.map (fun duration -> selected, input, duration)))))
