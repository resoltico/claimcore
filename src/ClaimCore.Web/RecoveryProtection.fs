namespace ClaimCore.Web

open System
open System.IO
open Microsoft.AspNetCore.DataProtection

/// Purpose-isolated request protection over an already-admitted private key-ring directory.
module internal RecoveryProtection =
    let create (directory: DirectoryInfo) : ClaimCore.Application.RecoveryProtection =
        let provider = DataProtectionProvider.Create directory
        let protector = provider.CreateProtector("ClaimCore.Recovery.Request")
        let prepare () = protector.Protect [||] |> ignore

        // Key availability cannot veto exact accepted-ledger replay through bearer authentication.
        try
            prepare ()
        with
        | :? System.Security.Cryptography.CryptographicException
        | :? IOException
        | :? UnauthorizedAccessException -> ()

        {
            Prepare = prepare
            Protect = fun purpose bytes -> protector.CreateProtector(purpose).Protect bytes
            Unprotect = fun purpose bytes -> protector.CreateProtector(purpose).Unprotect bytes
        }
