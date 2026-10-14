namespace ClaimCore.Contracts

open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open WireJson

type InvocationFrame =
    private
        {
            Endpoint: Endpoint
            Input: ServiceInput
            Bytes: byte array
            InputBytes: byte array
        }

module Frames =
    let endpoint frame = frame.Endpoint
    let input frame = frame.Input
    let bytes frame = Array.copy frame.Bytes
    let inputBytes frame = Array.copy frame.InputBytes

    let parse bytes =
        HttpInputSupport.parse bytes (fun root ->
            if not (SchemaValueValidation.verify EndpointCatalog.invocation root) then
                HttpInputSupport.fail HttpInputProblem.InvalidJson

            let id = root.GetProperty("endpoint").GetString() |> nonNull
            let endpoint = EndpointCatalog.tryFind id |> Option.get
            let input = root.GetProperty("input")

            {
                Endpoint = endpoint
                Input = ServiceInputs.read id input
                Bytes = Array.copy bytes
                InputBytes = Encoding.UTF8.GetBytes(input.GetRawText())
            })

    let decode bytes =
        HttpInputSupport.parse bytes (fun root ->
            if not (SchemaValueValidation.verify EndpointCatalog.frameResponse root) then
                HttpInputSupport.fail HttpInputProblem.InvalidJson

            root.Clone())

    let private encode (value: JsonNode) =
        let bytes = value.ToJsonString() |> Encoding.UTF8.GetBytes

        match decode bytes with
        | Ok _ -> bytes
        | Error _ -> invalidArg "value" "The frame does not satisfy the contract."

    let result endpoint response =
        match EndpointCatalog.admitResponse endpoint response with
        | Error _ -> Error HttpInputProblem.InvalidJson
        | Ok admitted ->
            Ok(
                encode (
                    obj
                        [
                            "kind", text "result"
                            "endpoint", text endpoint
                            "outcome",
                            (JsonNode.Parse(admitted.GetProperty("outcome").GetRawText()) |> nonNull)
                        ]
                )
            )

    let serviceFailure endpoint reason =
        encode (
            obj
                [
                    "kind", text "serviceFailure"
                    "endpoint", text endpoint
                    "failure", obj [ "code", text (HostFailures.code reason) ]
                ]
        )

    let localFailure code =
        encode (obj [ "kind", text "localFailure"; "code", text code ])

    let protocolFailure code =
        encode (
            obj
                [
                    "kind", text "protocolFailure"
                    "code", text code
                    "diagnosticId", text "PROTOCOL_INVALID_ENVELOPE"
                    "path", text "/"
                ]
        )

    let exitCode (frame: JsonElement) =
        match frame.GetProperty("kind").GetString() with
        | "protocolFailure" -> 2
        | "localFailure"
        | "serviceFailure" -> 3
        | "result" ->
            match frame.GetProperty("outcome").GetProperty("tag").GetString() with
            | "REJECTED" -> 2
            | "FAILED" -> 3
            | "OUTCOME_UNKNOWN" -> 4
            | "CANCELLED" -> 130
            | _ -> 0
        | _ -> invalidArg "frame" "Expected an admitted frame."
