namespace ClaimCore.Cli

open ClaimCore.ClientProtocol

open ClaimCore.ClientContract

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

    let protocol code = Frames.protocolFailure code |> admitted

    let local failure =
        let code, exit =
            match failure with
            | ClientFailure.Configuration -> "CLI_CONFIGURATION_INVALID", 3
            | ClientFailure.Authentication -> "CLI_AUTHENTICATION_FAILED", 3
            | ClientFailure.Unavailable -> "CLI_SERVICE_UNAVAILABLE", 3
            | ClientFailure.Cancelled -> "CLI_CANCELLED", 130
            | ClientFailure.Unexpected -> "CLI_INTERNAL", 70

        {
            Bytes = Frames.localFailure code
            ExitCode = exit
        }

    let forAttempt frame knowledge reply =
        ProtocolFrames.withKnowledge frame knowledge reply.Bytes |> admitted

    let unknown reply =
        let value =
            System.Text.Json.Nodes.JsonNode.Parse(System.ReadOnlySpan<byte> reply.Bytes)
            |> nonNull

        value["attemptKnowledge"] <- System.Text.Json.Nodes.JsonValue.Create("UNCONFIRMED")
        value.ToJsonString() |> System.Text.Encoding.UTF8.GetBytes |> admitted
