namespace ClaimCore.Protocol


[<RequireQualifiedAccess>]
type HostFailureReason =
    | InstallationMismatch
    | ContractMismatch
    | ConnectionRejected
    | OriginRejected
    | MediaTypeRejected
    | BodyTooLarge
    | SessionRejected
    | MethodRejected
    | AntiforgeryRejected
    | EndpointMissing
    | RateLimited
    | InputInvalid
    | BeforeDispatchFailed
    | DispatchUnconfirmed
    | CompletedResponseFailed
