namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type PrincipalKind =
    | Human
    | Service

/// Stable principal-kind syntax; admission and authority remain Application policy.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PrincipalKind =
    let vocabulary =
        Vocabulary.ofUnion<PrincipalKind> "token" TokenCasing.ScreamingSnake None

    let humanToken = Vocabulary.token vocabulary PrincipalKind.Human
    let serviceToken = Vocabulary.token vocabulary PrincipalKind.Service

    let all = Vocabulary.tokens vocabulary
