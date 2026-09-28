namespace ClaimCore.Database

open ClaimCore.HostSecurity

/// Private owner inputs only. Public keys are raw 32-byte Ed25519 values, never PEM guesses.
module internal DatabaseCopyInputs =
    let private exact maximum expected path =
        match PrivateFileService.readBinary maximum path with
        | Ok bytes when bytes.Length = expected -> Ok bytes
        | _ -> Error DatabaseInputProblem.ManagedCopyFileRefused

    let publicKey path = exact 32 32 path
    let signature path = exact 64 64 path

    let attestation path =
        match PrivateFileService.readBinary 300000 path with
        | Ok bytes when bytes.Length > 0 -> Ok bytes
        | _ -> Error DatabaseInputProblem.ManagedCopyFileRefused
