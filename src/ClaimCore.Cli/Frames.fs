namespace ClaimCore.Cli

open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.Contract.Protocol

open System

[<RequireQualifiedAccess>]
type DeliveryState =
    | NotSent
    | Delivered
    | Uncertain

[<RequireQualifiedAccess>]
type CliFailure =
    | ConfigurationInvalid
    | ConfigurationIssue of CliSetting * ConfigurationIssue
    | AuthenticationFailed
    | ServiceUnavailable
    | Cancelled
    | Internal
    | InvalidFrame
    | InvalidResponse
    | ContractMismatch

[<RequireQualifiedAccess>]
type CliFailureCode =
    | ConfigurationInvalid
    | AuthenticationFailed
    | ServiceUnavailable
    | Cancelled
    | Internal
    | InvalidFrame
    | InvalidResponse
    | ContractMismatch

module CliFailureCodes =
    let vocabulary =
        Vocabulary.define
            "cli-failure"
            [
                CliFailureCode.ConfigurationInvalid
                CliFailureCode.AuthenticationFailed
                CliFailureCode.ServiceUnavailable
                CliFailureCode.Cancelled
                CliFailureCode.Internal
                CliFailureCode.InvalidFrame
                CliFailureCode.InvalidResponse
                CliFailureCode.ContractMismatch
            ]
            (function
             | CliFailureCode.ConfigurationInvalid -> "CLI_CONFIGURATION_INVALID"
             | CliFailureCode.AuthenticationFailed -> "CLI_AUTHENTICATION_FAILED"
             | CliFailureCode.ServiceUnavailable -> "CLI_SERVICE_UNAVAILABLE"
             | CliFailureCode.Cancelled -> "CLI_CANCELLED"
             | CliFailureCode.Internal -> "CLI_INTERNAL"
             | CliFailureCode.InvalidFrame -> "INVALID_FRAME"
             | CliFailureCode.InvalidResponse -> "INVALID_RESPONSE"
             | CliFailureCode.ContractMismatch -> "CONTRACT_MISMATCH")

    let token = Vocabulary.token vocabulary
    let codes = Vocabulary.tokens vocabulary

    let ofFailure =
        function
        | CliFailure.ConfigurationInvalid
        | CliFailure.ConfigurationIssue _ -> CliFailureCode.ConfigurationInvalid
        | CliFailure.AuthenticationFailed -> CliFailureCode.AuthenticationFailed
        | CliFailure.ServiceUnavailable -> CliFailureCode.ServiceUnavailable
        | CliFailure.Cancelled -> CliFailureCode.Cancelled
        | CliFailure.Internal -> CliFailureCode.Internal
        | CliFailure.InvalidFrame -> CliFailureCode.InvalidFrame
        | CliFailure.InvalidResponse -> CliFailureCode.InvalidResponse
        | CliFailure.ContractMismatch -> CliFailureCode.ContractMismatch

    let toFailure =
        function
        | CliFailureCode.ConfigurationInvalid -> CliFailure.ConfigurationInvalid
        | CliFailureCode.AuthenticationFailed -> CliFailure.AuthenticationFailed
        | CliFailureCode.ServiceUnavailable -> CliFailure.ServiceUnavailable
        | CliFailureCode.Cancelled -> CliFailure.Cancelled
        | CliFailureCode.Internal -> CliFailure.Internal
        | CliFailureCode.InvalidFrame -> CliFailure.InvalidFrame
        | CliFailureCode.InvalidResponse -> CliFailure.InvalidResponse
        | CliFailureCode.ContractMismatch -> CliFailure.ContractMismatch

[<RequireQualifiedAccess; NoComparison>]
type FrameResult =
    | Reply of ServiceReply
    | HostFailure of HostFailure
    | LocalFailure of CliFailure

[<NoComparison>]
type FrameReply =
    {
        Endpoint: EndpointId option
        OperationId: Guid option
        Delivery: DeliveryState
        Result: FrameResult
    }

[<NoEquality; NoComparison>]
type Invocation =
    private
        {
            Target: ClaimCore.Contract.Route
            Input: JsonValue
            Bytes: byte array
        }

module Frames =
    let private error path reason = Error { Path = path; Reason = reason }

    let private field name codec document =
        match JsonValue.tryMember name document with
        | Some value -> codec.Read ("$." + name) value
        | None -> error ("$." + name) "missing member"

    let private members names =
        function
        | JsonValue.Object fields -> fields |> List.map fst |> Set.ofList = Set.ofList names
        | _ -> false

    let endpoint (invocation: Invocation) = invocation.Target
    let input (invocation: Invocation) = invocation.Input
    let inputBytes (invocation: Invocation) = Array.copy invocation.Bytes

    let operationId invocation =
        JsonValue.tryMember "operationId" (input invocation)
        |> Option.bind (fun value -> Codec.uuid.Read "$.operationId" value |> Result.toOption)

    let admit bytes =
        StrictJson.read Limits.MaximumInvocationBytes bytes
        |> Result.bind (fun document ->
            if not (members [ "endpoint"; "input" ] document) then
                error "$" "expected a frame"
            else
                match
                    field "endpoint" (Codec.boundedText 1 80) document,
                    JsonValue.tryMember "input" document
                with
                | Ok name, Some input ->
                    match Api.tryFind name with
                    | None -> error "$.endpoint" "unknown endpoint"
                    | Some endpoint ->
                        Api.admitInput endpoint input
                        |> Result.bind (fun () ->
                            let prepared = WireWriter.bytes input

                            if prepared.Length > Limits.MaximumRequestBytes then
                                error "$.input" "request exceeds its byte limit"
                            else
                                Ok
                                    {
                                        Target = endpoint
                                        Input = input
                                        Bytes = prepared
                                    })
                | Error problem, _ -> Error problem
                | _ -> error "$.input" "missing input")

    let encodeInvocation invocation =
        JsonValue.Object
            [
                "endpoint", JsonValue.String(Api.id (endpoint invocation))
                "input", input invocation
            ]
        |> WireWriter.bytes

    let private delivery =
        Vocabulary.define
            "delivery"
            [ DeliveryState.NotSent; DeliveryState.Delivered; DeliveryState.Uncertain ]
            (function
             | DeliveryState.NotSent -> "NOT_SENT"
             | DeliveryState.Delivered -> "DELIVERED"
             | DeliveryState.Uncertain -> "UNCERTAIN")
        |> Vocabulary.entries
        |> Codec.enumeration

    let failureCodes = CliFailureCodes.codes

    let failureCode failure =
        failure |> CliFailureCodes.ofFailure |> CliFailureCodes.token

    let private simpleFailure =
        Codec.record CliFailureCodes.toFailure
        |> Codec.field
            "code"
            CliFailureCodes.ofFailure
            (Codec.enumeration (Vocabulary.entries CliFailureCodes.vocabulary))
        |> Codec.build

    let private configurationFailure =
        Codec.record (fun setting issue -> setting, issue)
        |> Protocol.ResponseScalars.fixedField
            "code"
            (CliFailureCodes.token CliFailureCode.ConfigurationInvalid)
        |> Codec.field "setting" fst (Codec.enumeration InputDiagnostics.settings)
        |> Codec.field "issue" snd (Codec.enumeration InputDiagnostics.configurationIssues)
        |> Codec.build

    let failureCodec =
        Codec.choice
            "CliFailure"
            [
                Codec.branch
                    id
                    (function
                    | CliFailure.ConfigurationIssue _ -> None
                    | failure -> Some failure)
                    simpleFailure
                Codec.branch
                    (fun (setting, issue) -> CliFailure.ConfigurationIssue(setting, issue))
                    (function
                    | CliFailure.ConfigurationIssue(setting, issue) -> Some(setting, issue)
                    | _ -> None)
                    configurationFailure
            ]

    let private knownEndpoint =
        function
        | None -> Ok None
        | Some id ->
            match Api.tryFind id with
            | Some endpoint -> Ok(Some endpoint)
            | None -> error "$.endpoint" "unknown endpoint"

    let private readResult target value =
        match value with
        | JsonValue.Object [ "hostFailure", failure ] ->
            HostFailureCodecs.failure.Read "$.result.hostFailure" failure
            |> Result.map FrameResult.HostFailure
        | JsonValue.Object [ "localFailure", failure ] ->
            failureCodec.Read "$.result.localFailure" failure
            |> Result.map FrameResult.LocalFailure
        | reply ->
            match target with
            | Some endpoint -> ServiceReply.read endpoint reply |> Result.map FrameResult.Reply
            | None -> error "$.result" "a reply requires an endpoint"

    let private read document =
        if not (members [ "endpoint"; "operationId"; "delivery"; "result" ] document) then
            error "$" "expected a reply frame"
        else
            match
                field "endpoint" (Codec.nullable (Codec.boundedText 1 80)) document,
                field "operationId" (Codec.nullable Codec.uuid) document,
                field "delivery" delivery document
            with
            | Ok id, Ok operation, Ok delivered ->
                knownEndpoint id
                |> Result.bind (fun endpoint ->
                    match JsonValue.tryMember "result" document with
                    | None -> error "$.result" "missing result"
                    | Some result ->
                        readResult endpoint result
                        |> Result.map (fun result ->
                            {
                                Endpoint = endpoint |> Option.map _.Id
                                OperationId = operation
                                Delivery = delivered
                                Result = result
                            }))
            | Error problem, _, _
            | _, Error problem, _
            | _, _, Error problem -> Error problem

    let decode bytes =
        StrictJson.read Limits.MaximumResponseBytes bytes |> Result.bind read

    let private resultValue =
        function
        | FrameResult.Reply value -> ServiceReply.json value
        | FrameResult.HostFailure failure ->
            JsonValue.Object [ "hostFailure", HostFailureCodecs.failure.Write failure ]
        | FrameResult.LocalFailure failure ->
            JsonValue.Object [ "localFailure", failureCodec.Write failure ]

    let encode frame =
        let value =
            JsonValue.Object
                [
                    "endpoint",
                    (Codec.nullable (Codec.boundedText 1 80))
                        .Write(frame.Endpoint |> Option.map EndpointIds.token)
                    "operationId", (Codec.nullable Codec.uuid).Write frame.OperationId
                    "delivery", delivery.Write frame.Delivery
                    "result", resultValue frame.Result
                ]

        match frame.Result with
        | FrameResult.Reply reply when frame.Endpoint <> Some reply.Endpoint ->
            invalidArg "frame" "Reply endpoint and typed value differ."
        | _ -> ()

        let bytes = WireWriter.bytes value

        if bytes.Length > Limits.MaximumResponseBytes then
            invalidArg "frame" "Reply frame exceeds its byte limit."

        bytes

    let outcome reply =
        JsonValue.tryMember "outcome" reply
        |> Option.bind (JsonValue.tryMember "kind")
        |> Option.bind (function
            | JsonValue.String tag -> Some tag
            | _ -> None)

    let private mutation frame =
        frame.Endpoint
        |> Option.exists (fun id ->
            let endpoint =
                match id with
                | EndpointId.Host host -> Api.forHost host
                | EndpointId.Actor actor -> Api.forActor actor

            Api.executesLedger endpoint)

    let private outcomeExitCode reply =
        match outcome reply with
        | Some "REJECTED"
        | Some "NOT_ACCEPTED" -> 2
        | Some "FAILED" -> 3
        | Some "OUTCOME_UNKNOWN" -> 4
        | _ -> 0

    let exitCode frame =
        if frame.Delivery = DeliveryState.Uncertain && mutation frame then
            4
        else
            match frame.Result with
            | FrameResult.LocalFailure CliFailure.Cancelled -> 130
            | FrameResult.LocalFailure CliFailure.Internal -> 70
            | FrameResult.LocalFailure(CliFailure.InvalidFrame | CliFailure.InvalidResponse | CliFailure.ContractMismatch) ->
                2
            | FrameResult.LocalFailure _
            | FrameResult.HostFailure _ -> 3
            | FrameResult.Reply reply -> outcomeExitCode (ServiceReply.json reply)
