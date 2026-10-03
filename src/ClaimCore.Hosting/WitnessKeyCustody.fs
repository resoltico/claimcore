namespace ClaimCore.Hosting

open System
open System.Security.Cryptography
open ClaimCore.HostSecurity
open ClaimCore.Witness

/// Loads witness keys only from a handle-verified owner-private regular file.
module internal WitnessKeyCustody =
    let load path =
        match PrivateFileService.readBinary 32768 path with
        | Error _ -> invalidOp "Private witness key ring is unavailable."
        | Ok bytes ->
            try
                try
                    KeyRingCodec.parse bytes
                with _ ->
                    invalidOp "Private witness key ring is invalid."
            finally
                CryptographicOperations.ZeroMemory(bytes)
