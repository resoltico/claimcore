namespace ClaimCore.Catalog

/// Stable principal-kind syntax; identity admission and human-only authority remain Application policy.
module PrincipalKinds =
    [<Literal>]
    let Human = "HUMAN"

    [<Literal>]
    let Service = "SERVICE"

    let all = [ Human; Service ]
