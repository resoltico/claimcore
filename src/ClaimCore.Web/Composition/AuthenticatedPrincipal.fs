namespace ClaimCore.Web

open ClaimCore.Domain

open ClaimCore.Application
open ClaimCore.Application.Contracts

/// Verified identity facts admitted by the HTTPS authentication boundary.
[<StructuralEquality; NoComparison>]
type AuthenticatedPrincipal = private AuthenticatedPrincipal of PrincipalKey * RequesterClaims

module AuthenticatedPrincipals =
    let human issuer subject =
        PrincipalKey.human issuer subject
        |> Result.map (fun key -> AuthenticatedPrincipal(key, RequesterClaims.empty))
        |> Result.mapError (fun _ -> AuthenticationFailure.ActorPrincipalInvalid)

    let service issuer subject =
        PrincipalKey.service issuer subject
        |> Result.map (fun key -> AuthenticatedPrincipal(key, RequesterClaims.empty))
        |> Result.mapError (fun _ -> AuthenticationFailure.ActorPrincipalInvalid)

    let internal key (AuthenticatedPrincipal(principal, _)) = principal
    let internal claims (AuthenticatedPrincipal(_, claims)) = claims

    let internal withClaims claims (AuthenticatedPrincipal(principal, _)) =
        AuthenticatedPrincipal(principal, claims)
