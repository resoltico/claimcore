namespace ClaimCore.Catalog

[<RequireQualifiedAccess>]
type HostFailure =
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

module HostFailureIds =
    let private definitions =
        [
            HostFailure.ContractMismatch, "CONTRACT_MISMATCH", "HOST_CONTRACTMISMATCH"
            HostFailure.ConnectionRejected, "CONNECTION_REJECTED", "HOST_CONNECTIONREJECTED"
            HostFailure.OriginRejected, "ORIGIN_REJECTED", "HOST_ORIGINREJECTED"
            HostFailure.MediaTypeRejected, "MEDIA_TYPE_REJECTED", "HOST_MEDIATYPEREJECTED"
            HostFailure.BodyTooLarge, "BODY_TOO_LARGE", "HOST_BODYTOOLARGE"
            HostFailure.SessionRejected, "SESSION_REJECTED", "HOST_SESSIONREJECTED"
            HostFailure.SessionForbidden, "SESSION_FORBIDDEN", "HOST_SESSIONFORBIDDEN"
            HostFailure.MethodRejected, "METHOD_REJECTED", "HOST_METHODREJECTED"
            HostFailure.AntiforgeryRejected, "ANTIFORGERY_REJECTED", "HOST_ANTIFORGERYREJECTED"
            HostFailure.EndpointMissing, "ENDPOINT_MISSING", "HOST_ENDPOINTMISSING"
            HostFailure.RateLimited, "RATE_LIMITED", "HOST_RATELIMITED"
            HostFailure.InputInvalid, "INPUT_INVALID", "HOST_INPUTINVALID"
            HostFailure.BeforeDispatchFailed, "INTERNAL", "HOST_BEFOREDISPATCHFAILED"
            HostFailure.DispatchUnconfirmed, "INTERNAL", "HOST_DISPATCHUNCONFIRMED"
            HostFailure.CompletedResponseFailed, "RESPONSE_FAILED", "HOST_COMPLETEDRESPONSEFAILED"
        ]

    let token failure =
        definitions
        |> List.find (fun (value, _, _) -> value = failure)
        |> fun (_, code, _) -> code

    let diagnostic failure =
        definitions
        |> List.find (fun (value, _, _) -> value = failure)
        |> fun (_, _, diagnostic) -> diagnostic
