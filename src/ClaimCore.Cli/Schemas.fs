namespace ClaimCore.Cli

open ClaimCore.Contract
open ClaimCore.Json
open ClaimCore.Contract.Protocol

/// Offline frame schemas derive endpoint payloads from the same registry as admission.
module CliSchemas =
    let invocation =
        Api.all
        |> List.map (fun endpoint ->
            endpoint.Accept
                { new IEndpointVisitor<Schema> with
                    member _.Visit endpoint =
                        Schema.Object(
                            [
                                "endpoint", Schema.Const endpoint.Id
                                "input", endpoint.Input.Schema
                            ],
                            []
                        )
                })
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
                endpoint.Accept
                    { new IEndpointVisitor<Schema> with
                        member _.Visit endpoint =
                            frame (Schema.Const endpoint.Id) (endpoint.Output.Schema :: failures)
                    })

        Schema.OneOf(frame Schema.Null failures :: endpoints, None)

    let definitions =
        Frames.failureCodec.Definitions
        |> Map.fold
            (fun definitions key value ->
                match Map.tryFind key definitions with
                | Some existing when existing <> value ->
                    invalidOp "Frame schema definitions disagree."
                | _ -> Map.add key value definitions)
            OpenApi.definitions
        |> fun definitions -> SchemaJson.reachable definitions [ invocation; response ]
