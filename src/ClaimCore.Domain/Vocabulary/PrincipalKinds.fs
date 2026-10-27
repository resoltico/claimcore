namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type PrincipalKind =
    | Human
    | Service

/// Stable principal-kind syntax; admission and authority remain Application policy.
module PrincipalKinds =
    let vocabulary =
        Vocabulary.ofUnion<PrincipalKind> "token" TokenCasing.ScreamingSnake None

    let Human = Vocabulary.token vocabulary PrincipalKind.Human
    let Service = Vocabulary.token vocabulary PrincipalKind.Service

    let all = Vocabulary.tokens vocabulary
