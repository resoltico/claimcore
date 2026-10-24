namespace ClaimCore.Contract.Protocol

open ClaimCore.Contract

/// Fixed transport policy; presentation belongs to client catalogs.
module HostFailures =
    let private policies =
        [
            HostFailureReason.ContractMismatch,
            "CONTRACT_MISMATCH",
            409,
            Some ExecutionPhase.NotStarted
            HostFailureReason.ConnectionRejected,
            "CONNECTION_REJECTED",
            403,
            Some ExecutionPhase.NotStarted
            HostFailureReason.OriginRejected, "ORIGIN_REJECTED", 403, Some ExecutionPhase.NotStarted
            HostFailureReason.MediaTypeRejected,
            "MEDIA_TYPE_REJECTED",
            415,
            Some ExecutionPhase.NotStarted
            HostFailureReason.BodyTooLarge, "BODY_TOO_LARGE", 413, Some ExecutionPhase.NotStarted
            HostFailureReason.SessionRejected,
            "SESSION_REJECTED",
            401,
            Some ExecutionPhase.NotStarted
            HostFailureReason.SessionForbidden,
            "SESSION_FORBIDDEN",
            403,
            Some ExecutionPhase.NotStarted
            HostFailureReason.MethodRejected, "METHOD_REJECTED", 405, Some ExecutionPhase.NotStarted
            HostFailureReason.AntiforgeryRejected,
            "ANTIFORGERY_REJECTED",
            403,
            Some ExecutionPhase.NotStarted
            HostFailureReason.EndpointMissing,
            "ENDPOINT_MISSING",
            404,
            Some ExecutionPhase.NotStarted
            HostFailureReason.RateLimited, "RATE_LIMITED", 429, Some ExecutionPhase.NotStarted
            HostFailureReason.InputInvalid, "INPUT_INVALID", 400, Some ExecutionPhase.NotStarted
            HostFailureReason.BeforeDispatchFailed, "INTERNAL", 500, Some ExecutionPhase.NotStarted
            HostFailureReason.DispatchUnconfirmed,
            "INTERNAL",
            500,
            Some ExecutionPhase.StartedUnconfirmed
            HostFailureReason.CompletedResponseFailed, "RESPONSE_FAILED", 500, None
        ]

    let all = policies |> List.map (fun (reason, _, _, _) -> reason)

    let private policy reason =
        policies |> List.find (fun (value, _, _, _) -> value = reason)

    let code reason =
        let _, code, _, _ = policy reason in code

    let status reason =
        let _, _, status, _ = policy reason in status

    let phase reason =
        let _, _, _, phase = policy reason in phase
