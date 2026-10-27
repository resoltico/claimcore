namespace ClaimCore.Web

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type AuthenticationFailure =
    | ClaimAmbiguous
    | ClientInvalid
    | MetadataInvalid
    | PrincipalInvalid
    | PrincipalUnauthenticated
    | SubjectInvalid
    | UriInvalid
    | ActorPrincipalInvalid

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module AuthenticationFailure =
    let vocabulary =
        Vocabulary.ofUnion<AuthenticationFailure>
            "AuthenticationFailure"
            TokenCasing.ScreamingSnake
            (Some "OIDC_")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
