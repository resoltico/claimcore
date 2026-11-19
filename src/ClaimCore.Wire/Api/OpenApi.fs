namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

open ClaimCore.Json

open System.Net.Http

/// JSON and complete-object media share the installation and host failure contracts.
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

    let private withHostResponses success =
        let failures =
            hostResponses
            |> List.map (fun (code, name, _) ->
                code, object [ "$ref", text ("#/components/responses/" + name) ])

        object (("200", success) :: failures)

    let private responses output =
        withHostResponses (object [ "description", text "HTTP 200"; "content", content output ])

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
                    "description", text "HTTP 200"
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
                                    "x-claimcore-csv-success-only", JsonValue.Boolean true
                                    "schema", object [ "const", text "identity" ]
                                ]
                            "Content-Disposition",
                            object
                                [
                                    "x-claimcore-csv-success-only", JsonValue.Boolean true
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

    let private attachmentBinaryResponse transfer =
        let media =
            match transfer with
            | AttachmentTransfer.Download -> [ AttachmentTransport.downloadMediaType ]
            | AttachmentTransfer.Preview ->
                [ AttachmentTransport.pngMediaType; AttachmentTransport.jpegMediaType ]
            | AttachmentTransfer.Upload -> invalidArg (nameof transfer) "Upload replies are JSON."

        let responseHeader shape =
            object [ "schema", shape; "x-claimcore-binary-success-only", JsonValue.Boolean true ]

        object
            [
                "description", text "HTTP 200"
                "headers",
                object
                    [
                        "Content-Length",
                        schema (Schema.Integer(0L, int64 AttachmentLimits.FileBytes))
                        |> responseHeader
                        "Content-Encoding", schema (Schema.Const "identity") |> responseHeader
                        "ClaimCore-Installation",
                        object
                            [
                                "required", JsonValue.Boolean true
                                "schema", schema Codec.uuid.Schema
                            ]
                        AttachmentTransport.digestHeader,
                        object
                            [
                                "type", text "string"
                                "pattern", text "^sha-256=:[A-Za-z0-9+/]{43}=:$"
                            ]
                        |> responseHeader
                        "Cache-Control",
                        schema (Schema.Const "no-store, no-transform, max-age=0") |> responseHeader
                        if transfer = AttachmentTransfer.Download then
                            "Content-Disposition",
                            object [ "type", text "string" ] |> responseHeader
                    ]
                "content",
                object (
                    ("application/json",
                     object [ "schema", schema AttachmentTransport.failure.Schema ])
                    :: (media |> List.map (fun mediaType -> mediaType, object []))
                )
            ]

    let private attachmentUploadContent =
        let encoding media filename =
            object [ "contentType", text media; "x-claimcore-filename", text filename ]

        let uploadTask =
            ClaimCore.Domain.Tasks.Registry.clientDeclarations
            |> List.filter (fun declaration ->
                declaration.Execution = TaskExecution.AttachmentUpload)
            |> List.exactlyOne
            |> _.Id
            |> TaskId.token

        let manifest =
            object
                [
                    "allOf",
                    array
                        [
                            schema Api.operationExecute.Input.Schema
                            object
                                [
                                    "properties",
                                    object [ "task", object [ "const", text uploadTask ] ]
                                ]
                        ]
                ]

        object
            [
                AttachmentTransport.multipartMediaType,
                object
                    [
                        "schema",
                        object
                            [
                                "type", text "object"
                                "required",
                                array (
                                    List.map
                                        text
                                        [
                                            AttachmentTransport.manifestPartName
                                            AttachmentTransport.filePartName
                                        ]
                                )
                                "additionalProperties", JsonValue.Boolean false
                                "properties",
                                object
                                    [
                                        AttachmentTransport.manifestPartName, manifest
                                        AttachmentTransport.filePartName,
                                        object [ "type", text "string"; "format", text "binary" ]
                                    ]
                            ]
                        "encoding",
                        object
                            [
                                AttachmentTransport.manifestPartName,
                                encoding
                                    AttachmentTransport.manifestMediaType
                                    AttachmentTransport.manifestFileName
                                AttachmentTransport.filePartName,
                                encoding
                                    AttachmentTransport.downloadMediaType
                                    AttachmentTransport.fileName
                            ]
                        "x-claimcore-part-order",
                        array (
                            List.map
                                text
                                [
                                    AttachmentTransport.manifestPartName
                                    AttachmentTransport.filePartName
                                ]
                        )
                        "x-claimcore-max-request-byte-length",
                        JsonValue.Integer(
                            int64 (
                                AttachmentTransport.maximumUploadBytes Limits.MaximumRequestBytes
                            )
                        )
                    ]
            ]

    let private attachmentOperation transfer =
        let id = AttachmentTransport.token transfer
        let upload = transfer = AttachmentTransfer.Upload

        let result =
            if upload then
                withHostResponses (
                    object
                        [
                            "description", text "HTTP 200"
                            "headers",
                            object
                                [
                                    "ClaimCore-Installation",
                                    object
                                        [
                                            "required", JsonValue.Boolean true
                                            "schema", schema Codec.uuid.Schema
                                        ]
                                ]
                            "content", content Api.operationExecute.Output.Schema
                        ]
                )
            else
                withHostResponses (attachmentBinaryResponse transfer)

        object
            [
                "operationId", text id
                "x-claimcore-effect",
                text (
                    EndpointEffect.token (
                        if upload then
                            EndpointEffect.LedgerExecution
                        else
                            EndpointEffect.Read
                    )
                )
                "parameters",
                array
                    [
                        header "ClaimCore-Contract" true ResponseScalars.digest.Schema
                        header "ClaimCore-Installation" true Codec.uuid.Schema
                        header "X-ClaimCore-Antiforgery" false ResponseScalars.nonempty.Schema
                        if upload then
                            representationHeader
                    ]
                "security", security id
                "requestBody",
                object
                    [
                        "required", JsonValue.Boolean true
                        "content",
                        if upload then
                            attachmentUploadContent
                        else
                            content AttachmentTransport.request.Schema
                    ]
                "responses", result
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
            @ [
                ExportTransport.request.Schema
                ExportTransport.failure.Schema
                AttachmentTransport.request.Schema
                AttachmentTransport.failure.Schema
            ]

        endpoints
        |> List.fold
            (fun state (_, _, _, definitions) -> SchemaDefinitions.merge state definitions)
            (SchemaDefinitions.merge
                HostFailureCodecs.reply.Definitions
                ([
                    ExportTransport.request.Definitions
                    ExportTransport.failure.Definitions
                    AttachmentTransport.request.Definitions
                    AttachmentTransport.failure.Definitions
                 ]
                 |> List.fold SchemaDefinitions.merge Map.empty))
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
                        paths
                        @ [ ExportTransport.path, object [ "post", exportOperation ] ]
                        @ (AttachmentTransport.all
                           |> List.map (fun transfer ->
                               AttachmentTransport.path transfer,
                               object [ "post", attachmentOperation transfer ]))
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
