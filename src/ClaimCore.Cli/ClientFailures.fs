namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Protocol

type ClientFailure =
    | Configuration
    | ConfigurationIssue of CliSetting * ConfigurationIssue
    | Authentication
    | Unavailable
    | Cancelled
    | Unexpected

[<NoComparison>]
type CliReply =
    {
        Frame: FrameReply
        Bytes: byte array
        ExitCode: int
    }

module CliReply =
    let private encoded frame =
        {
            Frame = frame
            Bytes = Frames.encode frame
            ExitCode = Frames.exitCode frame
        }

    let private decode (reply: CliReply) = reply.Frame

    let admitted bytes =
        let frame =
            Frames.decode bytes
            |> Result.defaultWith (fun _ -> invalidArg "bytes" "Expected an admitted CLI reply.")

        {
            Frame = frame
            Bytes = bytes
            ExitCode = Frames.exitCode frame
        }

    let private initial result =
        {
            Endpoint = None
            OperationId = None
            Delivery = DeliveryState.NotSent
            Result = result
        }

    let protocol code =
        initial (FrameResult.LocalFailure code) |> encoded

    let local failure =
        let code =
            match failure with
            | ClientFailure.Configuration -> CliFailure.ConfigurationInvalid
            | ClientFailure.ConfigurationIssue(setting, issue) ->
                CliFailure.ConfigurationIssue(setting, issue)
            | ClientFailure.Authentication -> CliFailure.AuthenticationFailed
            | ClientFailure.Unavailable -> CliFailure.ServiceUnavailable
            | ClientFailure.Cancelled -> CliFailure.Cancelled
            | ClientFailure.Unexpected -> CliFailure.Internal

        initial (FrameResult.LocalFailure code) |> encoded

    let private frame invocation delivery result =
        {
            Endpoint = Some (Frames.endpoint invocation).Id
            OperationId = Frames.operationId invocation
            Delivery = delivery
            Result = result
        }

    let result invocation reply =
        ServiceReply.read (Frames.endpoint invocation) reply
        |> Result.mapError (fun _ -> CliFailure.InvalidResponse)
        |> Result.map (fun typed ->
            let delivery =
                if Frames.outcome reply = Some "OUTCOME_UNKNOWN" then
                    DeliveryState.Uncertain
                else
                    DeliveryState.Delivered

            frame invocation delivery (FrameResult.Reply typed) |> encoded)

    let serviceFailure invocation (failure: HostFailure) =
        let delivery =
            if failure.Phase = Some ExecutionPhase.NotStarted then
                DeliveryState.Delivered
            else
                DeliveryState.Uncertain

        frame invocation delivery (FrameResult.HostFailure failure) |> encoded

    let forAttempt invocation delivery reply =
        let decoded = decode reply
        frame invocation delivery decoded.Result |> encoded

    let unknown reply =
        { decode reply with
            Delivery = DeliveryState.Uncertain
        }
        |> encoded
