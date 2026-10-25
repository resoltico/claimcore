namespace ClaimCore.Contract

open System.Net.Http
open ClaimCore.Contract.Protocol

/// The registry and its codecs determine the complete structural contract.
module OpenApi =
    let private prefix = "#/components/schemas/"
    let private text = JsonValue.String
    let private object = JsonValue.Object
    let private array = JsonValue.Array
    let private schema = SchemaJson.value prefix

    let private merge left right =
        Map.fold
            (fun state key value ->
                match Map.tryFind key state with
                | Some existing when existing <> value -> invalidOp "OpenAPI definitions disagree."
                | _ -> Map.add key value state)
            left
            right

    let private content shape =
        object [ "application/json", object [ "schema", schema shape ] ]

    let private hostContent status =
        object
            [
                "application/json",
                object
                    [
                        "schema",
                        object
                            [
                                "allOf",
                                array
                                    [
                                        schema HostFailureCodecs.reply.Schema
                                        schema (HostFailureCodecs.responseSchema status)
                                    ]
                            ]
                    ]
            ]

    let private header name required shape =
        object
            [
                "name", text name
                "in", text "header"
                "required", JsonValue.Boolean required
                "schema", schema shape
            ]

    let private security id =
        let cookie = [ "cookieAuth", array [] ]

        let interactive =
            if id = "session" then
                cookie
            else
                cookie @ [ "antiforgery", array [] ]

        let alternatives = [ object interactive ]

        let alternatives =
            if id = "session.logout" then
                alternatives
            else
                alternatives @ [ object [ "bearerAuth", array [] ] ]

        array (
            if id = "session" then
                object [] :: alternatives
            else
                alternatives
        )

    let private hostResponses =
        HostFailures.all
        |> List.map HostFailures.status
        |> List.distinct
        |> List.sort
        |> List.map (fun status ->
            let code = status.ToString(System.Globalization.CultureInfo.InvariantCulture)
            let name = "Host" + code

            code,
            name,
            object [ "description", text ("HTTP " + code); "content", hostContent status ])

    let private responses output =
        let success =
            "200", object [ "description", text "HTTP 200"; "content", content output ]

        let failures =
            hostResponses
            |> List.map (fun (code, name, _) ->
                code, object [ "$ref", text ("#/components/responses/" + name) ])

        object (success :: failures)

    let private operation (endpoint: Endpoint<'In, 'Out>) =
        let parameters = [ header "ClaimCore-Contract" true ResponseScalars.digest.Schema ]

        let parameters =
            if endpoint.Id = "session" then
                parameters
            else
                parameters
                @ [ header "X-ClaimCore-Antiforgery" false ResponseScalars.nonempty.Schema ]

        let fields =
            [
                "operationId", text endpoint.Id
                "x-claimcore-effect", text (EndpointEffects.token endpoint.Effect)
                "parameters", array parameters
                "security", security endpoint.Id
                "responses", responses endpoint.Output.Schema
            ]

        object (
            if endpoint.Method = HttpMethod.Get then
                fields
            else
                fields
                @ [
                    "requestBody",
                    object
                        [
                            "required", JsonValue.Boolean true
                            "content", content endpoint.Input.Schema
                        ]
                ]
        )

    let private inspected (endpoint: AnyEndpoint) =
        endpoint.Accept
            { new IEndpointVisitor<_> with
                member _.Visit value =
                    value.Id,
                    value.Method.Method.ToLowerInvariant(),
                    operation value,
                    merge value.Input.Definitions value.Output.Definitions
            }

    let private securitySchemes =
        object
            [
                "cookieAuth",
                object
                    [
                        "type", text "apiKey"
                        "in", text "cookie"
                        "name", text "__Host-ClaimCoreSession"
                    ]
                "antiforgery",
                object
                    [
                        "type", text "apiKey"
                        "in", text "header"
                        "name", text "X-ClaimCore-Antiforgery"
                    ]
                "bearerAuth", object [ "type", text "http"; "scheme", text "bearer" ]
            ]

    let private endpoints = Api.all |> List.map inspected

    let definitions =
        let roots =
            Api.all
            |> List.collect (fun endpoint ->
                endpoint.Accept
                    { new IEndpointVisitor<_> with
                        member _.Visit value =
                            [ value.Input.Schema; value.Output.Schema ]
                    })

        endpoints
        |> List.fold
            (fun state (_, _, _, definitions) -> merge state definitions)
            HostFailureCodecs.reply.Definitions
        |> fun definitions ->
            SchemaJson.reachable definitions (HostFailureCodecs.reply.Schema :: roots)

    let document =
        object
            [
                "openapi", text "3.1.0"
                "jsonSchemaDialect", text "https://json-schema.org/draft/2020-12/schema"
                "info", object [ "title", text "ClaimCore"; "version", text "1" ]
                "paths",
                object (
                    endpoints
                    |> List.map (fun (id, method, operation, _) ->
                        "/api/" + id, object [ method, operation ])
                )
                "components",
                object
                    [
                        "securitySchemes", securitySchemes
                        "responses",
                        object (
                            hostResponses |> List.map (fun (_, name, response) -> name, response)
                        )
                        "schemas",
                        object (
                            definitions
                            |> Map.toList
                            |> List.map (fun (name, shape) -> name, schema shape)
                        )
                    ]
            ]
