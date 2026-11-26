namespace ClaimCore.Wire

open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

[<RequireQualifiedAccess>]
type HostFailureCode =
    | InstallationMismatch
    | ContractMismatch
    | ConnectionRejected
    | OriginRejected
    | MediaTypeRejected
    | BodyTooLarge
    | SessionRejected
    | IdentityProviderUnavailable
    | IdentityProviderUntrusted
    | MethodRejected
    | AntiforgeryRejected
    | EndpointMissing
    | RateLimited
    | InputInvalid
    | Internal
    | ResponseFailed

/// Fixed transport policy; each reason owns its code, status and dispatch phase together.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module HostFailureCode =
    let vocabulary =
        Vocabulary.ofUnion<HostFailureCode> "host" TokenCasing.ScreamingSnake None


type Session =
    {
        Authenticated: bool
        ContractIdentity: string
        AntiforgeryToken: string option
    }

[<RequireQualifiedAccess>]
type LocalSignOutReason =
    | NoProviderSession
    | IdentityProviderUnavailable
    | IdentityProviderUntrusted

module LocalSignOutReason =
    let vocabulary =
        Vocabulary.ofUnion<LocalSignOutReason> "localSignOutReason" TokenCasing.ScreamingSnake None

[<RequireQualifiedAccess>]
type LogoutHandoff =
    | Provider of location: string
    | LocalOnly of reason: LocalSignOutReason

/// The public host failure carries no server prose or transport status.
type HostFailure =
    {
        Code: HostFailureCode
        Phase: ExecutionPhase option
    }
