namespace ClaimCore.Web

open ClaimCore.Application

/// Verified identity facts admitted by the HTTPS authentication boundary.
[<StructuralEquality; NoComparison>]
type AuthenticatedSubject = private AuthenticatedSubject of PrincipalKey

module AuthenticatedSubjects =
    let human issuer subject =
        PrincipalKey.human issuer subject
        |> Result.map AuthenticatedSubject
        |> Result.mapError (fun _ -> AuthenticationFailure.ActorPrincipalInvalid)

    let service issuer subject =
        PrincipalKey.service issuer subject
        |> Result.map AuthenticatedSubject
        |> Result.mapError (fun _ -> AuthenticationFailure.ActorPrincipalInvalid)

    let internal key (AuthenticatedSubject principal) = principal
