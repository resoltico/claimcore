namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type PrincipalKind =
    | Human
    | Service

/// Closed principal kinds; Domain admits the issuer and stable identifier.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PrincipalKind =
    let vocabulary =
        Vocabulary.ofUnion<PrincipalKind> "principalKind" TokenCasing.ScreamingSnake None

    let humanToken = Vocabulary.token vocabulary PrincipalKind.Human
    let serviceToken = Vocabulary.token vocabulary PrincipalKind.Service

    let all = Vocabulary.tokens vocabulary
