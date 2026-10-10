namespace ClaimCore.Contracts

/// Transport resource limits shared by native and generated browser consumers.
module TransportLimits =
    [<Literal>]
    let JsonResponseBytes = 16 * 1024 * 1024

    [<Literal>]
    let RecoveryArtifactBytes = 131072
