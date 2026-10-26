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

module AuthenticationFailures =
    let vocabulary =
        Vocabulary.define
            "AuthenticationFailure"
            [
                AuthenticationFailure.ClaimAmbiguous
                AuthenticationFailure.ClientInvalid
                AuthenticationFailure.MetadataInvalid
                AuthenticationFailure.PrincipalInvalid
                AuthenticationFailure.PrincipalUnauthenticated
                AuthenticationFailure.SubjectInvalid
                AuthenticationFailure.UriInvalid
                AuthenticationFailure.ActorPrincipalInvalid
            ]
            (function
             | AuthenticationFailure.ClaimAmbiguous -> "OIDC_CLAIM_AMBIGUOUS"
             | AuthenticationFailure.ClientInvalid -> "OIDC_CLIENT_INVALID"
             | AuthenticationFailure.MetadataInvalid -> "OIDC_METADATA_INVALID"
             | AuthenticationFailure.PrincipalInvalid -> "OIDC_PRINCIPAL_INVALID"
             | AuthenticationFailure.PrincipalUnauthenticated -> "OIDC_PRINCIPAL_UNAUTHENTICATED"
             | AuthenticationFailure.SubjectInvalid -> "OIDC_SUBJECT_INVALID"
             | AuthenticationFailure.UriInvalid -> "OIDC_URI_INVALID"
             | AuthenticationFailure.ActorPrincipalInvalid -> "ACTOR_PRINCIPAL_INVALID")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
