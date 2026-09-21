namespace ClaimCore.Cli

open System
open ClaimCore.HostSecurity

module PrivateFiles =
    let readSource maximum path =
        PrivateFileService.readUtf8Bytes maximum path

    let connection () =
        match Environment.GetEnvironmentVariable("CLAIMCORE_CONNECTION_FILE") with
        | null -> Error "Set CLAIMCORE_CONNECTION_FILE to a private application connection file."
        | path ->
            match PrivateFileService.readUtf8Text 8192 path with
            | Error _ ->
                Error
                    "The connection file must be an owner-only regular UTF-8 file at a safe absolute path."
            | Ok text ->
                let value = text.Trim()

                if String.IsNullOrWhiteSpace(value) then
                    Error "The connection file is empty."
                else
                    Ok value

    let writeNew destination bytes =
        PrivateFileService.writeNew 131072 destination bytes
