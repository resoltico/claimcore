namespace ClaimCore.Cli

open ClaimCore.Contract
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
type FrameResult =
    | Reply of JsonValue
    | HostFailure of HostFailure
    | LocalFailure of CliFailure

type FrameReply =
    {
        Endpoint: string option
        OperationId: Guid option
        Delivery: DeliveryState
        Result: FrameResult
    }

[<NoEquality; NoComparison>]
type Invocation =
    private
        {
            Target: AnyEndpoint
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
            [
                DeliveryState.NotSent, "NOT_SENT"
                DeliveryState.Delivered, "DELIVERED"
                DeliveryState.Uncertain, "UNCERTAIN"
            ]
        |> Vocabulary.entries
        |> Codec.enumeration

    let private localCodes =
        [
            CliFailure.ConfigurationInvalid, "CLI_CONFIGURATION_INVALID"
            CliFailure.AuthenticationFailed, "CLI_AUTHENTICATION_FAILED"
            CliFailure.ServiceUnavailable, "CLI_SERVICE_UNAVAILABLE"
            CliFailure.Cancelled, "CLI_CANCELLED"
            CliFailure.Internal, "CLI_INTERNAL"
            CliFailure.InvalidFrame, "INVALID_FRAME"
            CliFailure.InvalidResponse, "INVALID_RESPONSE"
            CliFailure.ContractMismatch, "CONTRACT_MISMATCH"
        ]

    let private simpleFailure =
        Codec.record id
        |> Codec.field "code" id (Codec.enumeration localCodes)
        |> Codec.build

    let private configurationFailure =
        Codec.record (fun setting issue -> setting, issue)
        |> Protocol.ResponseScalars.fixedField "code" "CLI_CONFIGURATION_INVALID"
        |> Codec.field "setting" fst (Codec.enumeration InputDiagnostics.settings)
        |> Codec.field "issue" snd (Codec.enumeration InputDiagnostics.configurationIssues)
        |> Codec.build

    let failureCodec =
        Codec.choice
            "CliFailure"
            [
                Codec.branch
                    id
                    (fun failure ->
                        if localCodes |> List.exists (fst >> (=) failure) then
                            Some failure
                        else
                            None)
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
            | Some endpoint ->
                Api.admitOutput endpoint reply |> Result.map (fun () -> FrameResult.Reply reply)
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
                                Endpoint = id
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
        | FrameResult.Reply value -> value
        | FrameResult.HostFailure failure ->
            JsonValue.Object [ "hostFailure", HostFailureCodecs.failure.Write failure ]
        | FrameResult.LocalFailure failure ->
            JsonValue.Object [ "localFailure", failureCodec.Write failure ]

    let encode frame =
        let value =
            JsonValue.Object
                [
                    "endpoint", (Codec.nullable Codec.text).Write frame.Endpoint
                    "operationId", (Codec.nullable Codec.uuid).Write frame.OperationId
                    "delivery", delivery.Write frame.Delivery
                    "result", resultValue frame.Result
                ]

        read value
        |> Result.defaultWith (fun _ -> invalidArg "frame" "Invalid reply frame.")
        |> ignore

        let bytes = WireWriter.bytes value

        if bytes.Length > Limits.MaximumResponseBytes then
            invalidArg "frame" "Reply frame exceeds its byte limit."

        bytes

    let outcome reply =
        JsonValue.tryMember "outcome" reply
        |> Option.bind (JsonValue.tryMember "tag")
        |> Option.bind (function
            | JsonValue.String tag -> Some tag
            | _ -> None)

    let private mutation frame =
        frame.Endpoint |> Option.bind Api.tryFind |> Option.exists Api.executesLedger

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
            | FrameResult.Reply reply -> outcomeExitCode reply
