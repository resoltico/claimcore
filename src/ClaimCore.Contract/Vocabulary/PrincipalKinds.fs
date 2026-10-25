namespace ClaimCore.Contract

/// Stable principal-kind syntax; identity admission and human-only authority remain Application policy.
module PrincipalKinds =
    [<Literal>]
    let Human = "HUMAN"

    [<Literal>]
    let Service = "SERVICE"

    let vocabulary = Vocabulary.define "token" [ Human, Human; Service, Service ]
    let all = Vocabulary.tokens vocabulary
