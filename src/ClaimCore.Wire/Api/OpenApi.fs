namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

open ClaimCore.Json

open System.Net.Http

/// The JSON registry and CSV media declaration determine the complete structural contract.
module OpenApi =
    let private prefix = "#/components/schemas/"
    let private text = JsonValue.String
    let private object = JsonValue.Object
    let private array = JsonValue.Array
    let private schema = SchemaJson.value prefix

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

    let private representationHeader =
        object
            [
                "name", text Representation.HeaderName
                "in", text "header"
                "required", JsonValue.Boolean false
                "description",
                text
                    "Select HUMAN documents or DATA projections. Missing selects DATA. Values are case-insensitive; repeated identical selections are allowed, unknown or conflicting selections refuse before dispatch."
                "schema",
                object
                    [
                        "type", text "string"
                        "enum",
                        array (
                            Vocabulary.all Representation.vocabulary
                            |> List.map (Representation.token >> text)
                        )
                        "default", text (Representation.token Representation.defaultSelection)
                    ]
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
        HostFailureReason.all
        |> List.map HostFailureReason.status
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

    let private operation (endpoint: Route) =
        let parameters = [ header "ClaimCore-Contract" true ResponseScalars.digest.Schema ]

        let parameters =
            if endpoint.Token = "session" then
                parameters
            else
                parameters
                @ [ header "X-ClaimCore-Antiforgery" false ResponseScalars.nonempty.Schema ]

        let parameters =
            match endpoint.Id with
            | EndpointId.Actor _ ->
                parameters
                @ [
                    header
                        "ClaimCore-Installation"
                        (Api.requiresInstallation endpoint)
                        Codec.uuid.Schema
                ]
            | EndpointId.Host _ -> parameters

        let parameters =
            if Api.requiresRepresentation endpoint then
                parameters @ [ representationHeader ]
            else
                parameters

        let fields =
            [
                "operationId", text endpoint.Token
                "x-claimcore-effect", text (EndpointEffect.token endpoint.Effect)
                "parameters", array parameters
                "security", security endpoint.Token
                "responses", responses endpoint.OutputSchema
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
                            "content", content endpoint.InputSchema
                        ]
                ]
        )

    /// CSV success and JSON failure are separate transport media, not semantic representations.
    let private exportOperation =
        let response =
            object
                [
                    "description",
                    text
                        "A complete bounded CSV document, or a typed JSON failure. No successful partial export is sent."
                    "headers",
                    object
                        [
                            "Content-Length",
                            object
                                [
                                    "required", JsonValue.Boolean true
                                    "schema",
                                    object
                                        [
                                            "type", text "integer"
                                            "minimum", JsonValue.Integer 0L
                                            "maximum",
                                            JsonValue.Integer(int64 Limits.MaximumResponseBytes)
                                        ]
                                ]
                            "Content-Encoding",
                            object
                                [
                                    "description", text "CSV success only"
                                    "schema", object [ "const", text "identity" ]
                                ]
                            "Content-Disposition",
                            object
                                [
                                    "description", text "CSV success only"
                                    "schema", object [ "type", text "string" ]
                                ]
                        ]
                    "content",
                    object
                        [
                            ExportTransport.successMediaType,
                            object [ "schema", object [ "type", text "string" ] ]
                            "application/json",
                            object [ "schema", schema ExportTransport.failure.Schema ]
                        ]
                ]

        object
            [
                "operationId", text ExportTransport.token
                "x-claimcore-effect", text (EndpointEffect.token EndpointEffect.Read)
                "parameters",
                array
                    [
                        header "ClaimCore-Contract" true ResponseScalars.digest.Schema
                        header "ClaimCore-Installation" true Codec.uuid.Schema
                        header "X-ClaimCore-Antiforgery" false ResponseScalars.nonempty.Schema
                    ]
                "security", security ExportTransport.token
                "requestBody",
                object
                    [
                        "required", JsonValue.Boolean true
                        "content", content ExportTransport.request.Schema
                    ]
                "responses",
                object (
                    ("200", response)
                    :: (hostResponses
                        |> List.map (fun (code, name, _) ->
                            code, object [ "$ref", text ("#/components/responses/" + name) ]))
                )
            ]

    let private inspected (endpoint: Route) =
        endpoint.Token,
        endpoint.Method.Method.ToLowerInvariant(),
        operation endpoint,
        SchemaDefinitions.merge endpoint.InputDefinitions endpoint.OutputDefinitions

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
            (Api.all
             |> List.collect (fun endpoint -> [ endpoint.InputSchema; endpoint.OutputSchema ]))
            @ [ ExportTransport.request.Schema; ExportTransport.failure.Schema ]

        endpoints
        |> List.fold
            (fun state (_, _, _, definitions) -> SchemaDefinitions.merge state definitions)
            (SchemaDefinitions.merge
                HostFailureCodecs.reply.Definitions
                (SchemaDefinitions.merge
                    ExportTransport.request.Definitions
                    ExportTransport.failure.Definitions))
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
                    |> fun paths ->
                        paths @ [ ExportTransport.path, object [ "post", exportOperation ] ]
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
