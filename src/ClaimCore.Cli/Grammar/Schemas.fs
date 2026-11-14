namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json

/// Offline frame schemas derive endpoint payloads from the same registry as admission.
module CliSchemas =
    let invocation =
        Api.all
        |> List.map (fun endpoint ->
            Schema.Object(
                ([ "endpoint", Schema.Const endpoint.Token; "input", endpoint.InputSchema ]
                 @ (if Api.requiresInstallation endpoint then
                        [ "installationId", Codec.uuid.Schema ]
                    else
                        [])),
                (if Api.requiresInstallation endpoint then
                     [ "installationId" ]
                 else
                     [])
            ))
        |> fun variants -> Schema.OneOf(variants, None)

    let private failures =
        [
            HostFailureCodecs.reply.Schema
            Schema.Object([ "localFailure", Frames.failureCodec.Schema ], [])
        ]

    let private frame endpoint results =
        Schema.Object(
            [
                "endpoint", endpoint
                "operationId", (Codec.nullable Codec.uuid).Schema
                "delivery",
                Schema.OneOf(
                    [ "NOT_SENT"; "DELIVERED"; "UNCERTAIN" ] |> List.map Schema.Const,
                    None
                )
                "result", Schema.OneOf(results, None)
            ],
            []
        )

    let response =
        let endpoints =
            Api.all
            |> List.map (fun endpoint ->
                frame (Schema.Const endpoint.Token) (endpoint.OutputSchema :: failures))

        Schema.OneOf(frame Schema.Null failures :: endpoints, None)

    let internal recoveryList =
        Codec.record id
        |> Codec.field
            "recovery"
            id
            (Codec.list System.Int32.MaxValue RecoveryJournal.identityCodec)
        |> Codec.build
        |> Codec.named "RecoveryList"

    let definitions =
        SchemaDefinitions.merge OpenApi.definitions Frames.failureCodec.Definitions
        |> fun definitions -> SchemaJson.reachable definitions [ invocation; response ]

    /// Named exports are shared by schema admission, help and lookup.
    let named =
        Map.ofList
            [
                "session.request", (Api.session.Input.Schema, Api.session.Input.Definitions)
                "invocation", (invocation, definitions)
                "response", (response, definitions)
                "recovery", (recoveryList.Schema, recoveryList.Definitions)
                "host-failure",
                (HostFailureCodecs.reply.Schema, HostFailureCodecs.reply.Definitions)
            ]

    let names =
        [
            "openapi"
            yield! Map.keys named
            for endpoint in Api.all do
                if Api.id endpoint <> "session" then
                    Api.id endpoint + ".request"

                Api.id endpoint + ".response"
            yield! Map.keys OpenApi.definitions
        ]
        |> List.distinct
        |> List.sort
