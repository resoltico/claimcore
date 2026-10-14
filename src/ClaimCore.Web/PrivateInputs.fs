namespace ClaimCore.Web

open System
open System.Text
open System.Security.Cryptography
open ClaimCore.HostSecurity

module internal PrivateInputs =
    let text limit path =
        match PrivateFile.read limit path with
        | Error reason -> Error reason
        | Ok bytes ->
            try
                try
                    Ok(UTF8Encoding(false, true).GetString bytes)
                with :? DecoderFallbackException ->
                    Error PrivateFileFailure.Unreadable
            finally
                CryptographicOperations.ZeroMemory(Span<byte>(bytes))
