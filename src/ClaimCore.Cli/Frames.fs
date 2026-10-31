namespace ClaimCore.Cli

open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Json

open System

[<RequireQualifiedAccess>]
type DeliveryState =
    | NotSent
    | Delivered
    | Uncertain

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module DeliveryState =
    let vocabulary =
        Vocabulary.ofUnion<DeliveryState> "delivery" TokenCasing.ScreamingSnake None

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type FrameResult =
    | Reply of ServiceReply
    | HostFailure of HostFailure
    | LocalFailure of CliFailure

[<NoEquality; NoComparison>]
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
            AttemptId: Guid
            InstallationId: Guid option
            Typed: IEndpointInput
            Target: ClaimCore.Protocol.Route
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

    let installationId (invocation: Invocation) = invocation.InstallationId

    let value endpoint (invocation: Invocation) =
        EndpointValues.tryInput endpoint invocation.Typed

    let bindInstallation installation (invocation: Invocation) =
        if installation = Guid.Empty || not (Api.requiresInstallation invocation.Target) then
            Error ClientFailure.InvalidFrame
        elif invocation.InstallationId |> Option.exists ((<>) installation) then
            Error ClientFailure.InstallationMismatch
        else
            Ok
                { invocation with
                    InstallationId = Some installation
                }

    let attemptId (invocation: Invocation) = invocation.AttemptId
    let endpoint (invocation: Invocation) = invocation.Target
    let input (invocation: Invocation) = invocation.Input
    let inputBytes (invocation: Invocation) = Array.copy invocation.Bytes

    let operationId invocation =
        JsonValue.tryMember "operationId" (input invocation)
        |> Option.bind (fun value -> Codec.uuid.Read "$.operationId" value |> Result.toOption)

    /// Typed in-process callers materialize once; only external frames require byte admission.
    let create (endpoint: Endpoint<'input, 'output>) input =
        try
            let value = endpoint.Input.Write input
            let bytes = WireWriter.bytes value

            if bytes.Length > Limits.MaximumRequestBytes then
                error "$.input" "request exceeds its byte limit"
            else
                Ok
                    {
                        AttemptId = Guid.NewGuid()
                        InstallationId = None
                        Typed = EndpointValues.input endpoint input
                        Target = ClaimCore.Protocol.Route.describe endpoint
                        Input = value
                        Bytes = bytes
                    }
        with :? ArgumentException ->
            error "$.input" "invalid request format"

    let fromInput (input: IEndpointInput) =
        input.Accept
            { new IEndpointInputVisitor<Result<Invocation, Problem>> with
                member _.Visit<'value, 'reply>(endpoint: Endpoint<'value, 'reply>, value: 'value) =
                    create endpoint value
            }

    let admit bytes =
        StrictJson.read Limits.MaximumInvocationBytes bytes
        |> Result.bind (fun document ->
            if
                not (
                    members [ "endpoint"; "input" ] document
                    || members [ "endpoint"; "input"; "installationId" ] document
                )
            then
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
                        endpoint.ReadInput input
                        |> Result.bind (fun typed ->
                            let prepared = WireWriter.bytes input

                            if prepared.Length > Limits.MaximumRequestBytes then
                                error "$.input" "request exceeds its byte limit"
                            else
                                Ok
                                    {
                                        AttemptId = Guid.NewGuid()
                                        InstallationId = None
                                        Typed = typed
                                        Target = endpoint
                                        Input = input
                                        Bytes = prepared
                                    })
                | Error problem, _ -> Error problem
                | _ -> error "$.input" "missing input"
            |> Result.bind (fun invocation ->
                match JsonValue.tryMember "installationId" document with
                | None -> Ok invocation
                | Some value ->
                    Codec.uuid.Read "$.installationId" value
                    |> Result.bind (fun installation ->
                        bindInstallation installation invocation
                        |> Result.mapError (fun _ ->
                            {
                                Path = "$.installationId"
                                Reason = "Invalid installation binding."
                            }))))

    let encodeInvocation invocation =
        JsonValue.Object
            [
                "endpoint", JsonValue.String(Api.id (endpoint invocation))
                "input", input invocation
                yield!
                    invocation.InstallationId
                    |> Option.map (fun id -> "installationId", Codec.uuid.Write id)
                    |> Option.toList
            ]
        |> WireWriter.bytes

    let private delivery =
        DeliveryState.vocabulary |> Vocabulary.entries |> Codec.enumeration

    let failureCodes = CliFailureCode.codes

    let failureCode failure =
        failure |> CliFailureCode.ofFailure |> CliFailureCode.token

    let private simpleFailure =
        Codec.record CliFailureCode.toFailure
        |> Codec.field
            "code"
            CliFailureCode.ofFailure
            (Codec.enumeration (Vocabulary.entries CliFailureCode.vocabulary))
        |> Codec.build

    let private configurationFailure =
        Codec.record (fun setting issue -> setting, issue)
        |> ResponseScalars.fixedField
            "code"
            (CliFailureCode.token CliFailureCode.ConfigurationInvalid)
        |> Codec.field "setting" fst (Codec.enumeration InputDiagnostics.settings)
        |> Codec.field "issue" snd (Codec.enumeration ConfigurationIssue.all)
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

            Api.requiresInstallation endpoint)

    let exitCode frame =
        (if frame.Delivery = DeliveryState.Uncertain && mutation frame then
             ExitCode.OutcomeUnknown
         else
             match frame.Result with
             | FrameResult.LocalFailure CliFailure.Cancelled -> ExitCode.Cancelled
             | FrameResult.LocalFailure CliFailure.Internal -> ExitCode.InternalError
             | FrameResult.LocalFailure(CliFailure.InvalidFrame | CliFailure.InvalidResponse | CliFailure.ContractMismatch) ->
                 ExitCode.Rejected
             | FrameResult.LocalFailure _
             | FrameResult.HostFailure _ -> ExitCode.Failed
             | FrameResult.Reply reply -> ServiceReply.exitCode reply)
        |> ExitCode.value
