namespace ClaimCore.Server

open ClaimCore.Domain

open ClaimCore.Application
open ClaimCore.Application.Contracts

/// Verified identity facts admitted by the HTTPS authentication boundary.
[<StructuralEquality; NoComparison>]
type AuthenticatedPrincipal =
    private | AuthenticatedPrincipal of PrincipalKey * RequesterClaims * VerifiedCredential option

module AuthenticatedPrincipals =
    let human issuer subject =
        PrincipalKey.human issuer subject
        |> Result.map (fun key -> AuthenticatedPrincipal(key, RequesterClaims.empty, None))
        |> Result.mapError (fun _ -> AuthenticationFailure.ActorPrincipalInvalid)

    let service issuer subject =
        PrincipalKey.service issuer subject
        |> Result.map (fun key -> AuthenticatedPrincipal(key, RequesterClaims.empty, None))
        |> Result.mapError (fun _ -> AuthenticationFailure.ActorPrincipalInvalid)

    let internal key (AuthenticatedPrincipal(principal, _, _)) = principal
    let internal claims (AuthenticatedPrincipal(_, claims, _)) = claims

    let internal withClaims claims (AuthenticatedPrincipal(principal, _, credential)) =
        AuthenticatedPrincipal(principal, claims, credential)

    let internal withCredential credential (AuthenticatedPrincipal(principal, claims, _)) =
        AuthenticatedPrincipal(principal, claims, Some credential)

    let internal serviceContext (AuthenticatedPrincipal(_, claims, credential)) core =
        let services = Service.withClaims claims core

        credential
        |> Option.fold (fun services proof -> Service.withCredential proof services) services
