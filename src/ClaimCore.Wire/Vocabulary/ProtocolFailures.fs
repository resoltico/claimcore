namespace ClaimCore.Wire

open ClaimCore.Presentation


[<RequireQualifiedAccess>]
type HostFailureReason =
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
    | BeforeDispatchFailed
    | DispatchUnconfirmed
    | CompletedResponseFailed
