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

module HostFailureIds =
    let private definitions =
        [
            HostFailureReason.ContractMismatch, "CONTRACT_MISMATCH", "HOST_CONTRACTMISMATCH"
            HostFailureReason.ConnectionRejected, "CONNECTION_REJECTED", "HOST_CONNECTIONREJECTED"
            HostFailureReason.OriginRejected, "ORIGIN_REJECTED", "HOST_ORIGINREJECTED"
            HostFailureReason.MediaTypeRejected, "MEDIA_TYPE_REJECTED", "HOST_MEDIATYPEREJECTED"
            HostFailureReason.BodyTooLarge, "BODY_TOO_LARGE", "HOST_BODYTOOLARGE"
            HostFailureReason.SessionRejected, "SESSION_REJECTED", "HOST_SESSIONREJECTED"
            HostFailureReason.SessionForbidden, "SESSION_FORBIDDEN", "HOST_SESSIONFORBIDDEN"
            HostFailureReason.MethodRejected, "METHOD_REJECTED", "HOST_METHODREJECTED"
            HostFailureReason.AntiforgeryRejected,
            "ANTIFORGERY_REJECTED",
            "HOST_ANTIFORGERYREJECTED"
            HostFailureReason.EndpointMissing, "ENDPOINT_MISSING", "HOST_ENDPOINTMISSING"
            HostFailureReason.RateLimited, "RATE_LIMITED", "HOST_RATELIMITED"
            HostFailureReason.InputInvalid, "INPUT_INVALID", "HOST_INPUTINVALID"
            HostFailureReason.BeforeDispatchFailed, "INTERNAL", "HOST_BEFOREDISPATCHFAILED"
            HostFailureReason.DispatchUnconfirmed, "INTERNAL", "HOST_DISPATCHUNCONFIRMED"
            HostFailureReason.CompletedResponseFailed,
            "RESPONSE_FAILED",
            "HOST_COMPLETEDRESPONSEFAILED"
        ]

    let token failure =
        definitions
        |> List.find (fun (value, _, _) -> value = failure)
        |> fun (_, code, _) -> code

    let diagnostic failure =
        definitions
        |> List.find (fun (value, _, _) -> value = failure)
        |> fun (_, _, diagnostic) -> diagnostic
