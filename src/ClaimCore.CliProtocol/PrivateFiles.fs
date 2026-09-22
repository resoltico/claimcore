namespace ClaimCore.Cli

open System
open ClaimCore.HostSecurity
open ClaimCore.Contracts

module PrivateFiles =
    let readSource maximum path =
        PrivateFileService.readUtf8Bytes maximum path

    let connection () =
        match Environment.GetEnvironmentVariable("CLAIMCORE_CONNECTION_FILE") with
        | null -> Error ProtocolProblem.ConnectionMissing
        | path ->
            match PrivateFileService.readUtf8Text 8192 path with
            | Error _ -> Error ProtocolProblem.ConnectionAccess
            | Ok text ->
                let value = text.Trim()

                if String.IsNullOrWhiteSpace(value) then
                    Error ProtocolProblem.ConnectionEmpty
                else
                    Ok value

    let writeNew destination bytes =
        PrivateFileService.writeNew 131072 destination bytes
