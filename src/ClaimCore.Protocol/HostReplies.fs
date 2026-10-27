namespace ClaimCore.Protocol

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type HostFailureCode =
    | ContractMismatch
    | ConnectionRejected
    | OriginRejected
    | MediaTypeRejected
    | BodyTooLarge
    | SessionRejected
    | SessionForbidden
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

/// The public host failure carries no server prose or transport status.
type HostFailure =
    {
        Code: HostFailureCode
        Phase: ExecutionPhase option
    }
