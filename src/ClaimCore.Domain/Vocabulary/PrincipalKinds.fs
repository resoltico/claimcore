namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type PrincipalKind =
    | Human
    | Service

/// Stable principal-kind syntax; admission and authority remain Application policy.
module PrincipalKinds =
    [<Literal>]
    let Human = "HUMAN"

    [<Literal>]
    let Service = "SERVICE"

    let vocabulary =
        Vocabulary.define "token" [ PrincipalKind.Human; PrincipalKind.Service ] (function
            | PrincipalKind.Human -> Human
            | PrincipalKind.Service -> Service)

    let all = Vocabulary.tokens vocabulary
