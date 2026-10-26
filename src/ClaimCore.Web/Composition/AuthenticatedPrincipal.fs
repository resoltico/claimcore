namespace ClaimCore.Web

open ClaimCore.Application

/// Verified identity facts admitted by the HTTPS authentication boundary.
[<StructuralEquality; NoComparison>]
type AuthenticatedPrincipal = private AuthenticatedPrincipal of PrincipalKey

module AuthenticatedPrincipals =
    let human issuer subject =
        PrincipalKey.human issuer subject
        |> Result.map AuthenticatedPrincipal
        |> Result.mapError (fun _ -> AuthenticationFailure.ActorPrincipalInvalid)

    let service issuer subject =
        PrincipalKey.service issuer subject
        |> Result.map AuthenticatedPrincipal
        |> Result.mapError (fun _ -> AuthenticationFailure.ActorPrincipalInvalid)

    let internal key (AuthenticatedPrincipal principal) = principal
