namespace ClaimCore.Server

open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.HostSecurity

module internal MasterKeyFile =
    [<Literal>]
    let setting = "CLAIMCORE_DATA_KEY_FILE"

    let read path =
        match PrivateFile.read MasterKey.Size path with
        | Error _ -> None
        | Ok bytes ->
            try
                MasterKey.admit bytes
            finally
                CryptographicOperations.ZeroMemory bytes
