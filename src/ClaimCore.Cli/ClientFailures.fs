namespace ClaimCore.Cli

open ClaimCore.Catalog

open ClaimCore.Wire

type ClientFailure =
    | Configuration
    | Authentication
    | Unavailable
    | Cancelled
    | Unexpected

type CliReply = { Bytes: byte array; ExitCode: int }

module CliReply =
    let admitted bytes =
        let frame =
            Frames.decode bytes
            |> Result.defaultWith (fun _ -> invalidArg "bytes" "Expected an admitted CLI reply.")

        {
            Bytes = bytes
            ExitCode = Frames.exitCode frame
        }

    let private encoded frame =
        {
            Bytes = Frames.encode frame
            ExitCode = Frames.exitCode frame
        }

    let private initial: ClaimCore.Wire.FrameDelivery =
        {
            OperationId = None
            AttemptKnowledge = AttemptKnowledge.NotSent
            ResolutionEvidence = ResolutionEvidence.NoEvidence
        }

    let protocol code =
        FrameReply.ProtocolFailure(initial, None, code) |> encoded

    let local failure =
        let code =
            match failure with
            | ClientFailure.Configuration -> LocalFailure.ConfigurationInvalid
            | ClientFailure.Authentication -> LocalFailure.AuthenticationFailed
            | ClientFailure.Unavailable -> LocalFailure.ServiceUnavailable
            | ClientFailure.Cancelled -> LocalFailure.Cancelled
            | ClientFailure.Unexpected -> LocalFailure.Internal

        FrameReply.LocalFailure(initial, None, code) |> encoded

    let private decode reply =
        Frames.decode reply.Bytes
        |> Result.defaultWith (fun _ -> invalidArg "reply" "Expected an admitted CLI reply.")

    let private operation invocation =
        match Frames.request invocation with
        | WireRequest.CasePreview value
        | WireRequest.CaseExecute value -> Some value.OperationId
        | WireRequest.AuthorityExecute value -> Some value.OperationId
        | WireRequest.OperationObserve value -> Some value.OperationId
        | _ -> None

    let private metadata invocation attempt evidence : ClaimCore.Wire.FrameDelivery =
        let endpoint = Frames.request invocation |> WireRequest.endpoint

        {
            OperationId = operation invocation
            AttemptKnowledge = Delivery.knowledge endpoint attempt
            ResolutionEvidence = evidence
        }

    let result invocation reply =
        let endpoint = Frames.request invocation |> WireRequest.endpoint
        let attempt = WireReply.attempt reply

        Frames.fromReply (metadata invocation attempt (Delivery.evidence endpoint attempt)) reply
        |> encoded

    let serviceFailure invocation (failure: HostFailureReply) =
        let endpoint = Frames.request invocation |> WireRequest.endpoint
        let attempt = AttemptResult.HostFailure(HostFailures.phase failure.Reason)

        FrameReply.ServiceFailure(
            metadata invocation attempt ResolutionEvidence.NoEvidence,
            endpoint,
            failure
        )
        |> encoded

    let forAttempt invocation knowledge reply =
        let frame = decode reply

        let meta =
            { Frames.metadata frame with
                OperationId = operation invocation
                AttemptKnowledge = knowledge
            }

        let endpoint =
            Frames.request invocation |> WireRequest.endpoint |> Endpoint.id |> Some

        match frame with
        | FrameReply.LocalFailure(_, _, code) -> FrameReply.LocalFailure(meta, endpoint, code)
        | FrameReply.ProtocolFailure(_, _, code) -> FrameReply.ProtocolFailure(meta, endpoint, code)
        | value -> Frames.withMetadata meta value
        |> encoded

    let unknown reply =
        let frame = decode reply

        Frames.withMetadata
            { Frames.metadata frame with
                AttemptKnowledge = AttemptKnowledge.Unconfirmed
            }
            frame
        |> encoded
