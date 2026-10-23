namespace ClaimCore.Contract

[<RequireQualifiedAccess>]
type HostFailureReason =
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
    | BeforeDispatchFailed
    | DispatchUnconfirmed
    | CompletedResponseFailed
