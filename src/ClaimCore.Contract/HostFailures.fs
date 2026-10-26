namespace ClaimCore.Contract.Protocol

open ClaimCore.Contract
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
module HostFailures =
    // Flat exhaustive transport policy; compiler coverage replaces a partial table lookup.
    // fsharplint:disable-next-line CyclomaticComplexity
    let private policy =
        function
        | HostFailureReason.ContractMismatch ->
            HostFailureCode.ContractMismatch, 409, Some ExecutionPhase.NotStarted
        | HostFailureReason.ConnectionRejected ->
            HostFailureCode.ConnectionRejected, 403, Some ExecutionPhase.NotStarted
        | HostFailureReason.OriginRejected ->
            HostFailureCode.OriginRejected, 403, Some ExecutionPhase.NotStarted
        | HostFailureReason.MediaTypeRejected ->
            HostFailureCode.MediaTypeRejected, 415, Some ExecutionPhase.NotStarted
        | HostFailureReason.BodyTooLarge ->
            HostFailureCode.BodyTooLarge, 413, Some ExecutionPhase.NotStarted
        | HostFailureReason.SessionRejected ->
            HostFailureCode.SessionRejected, 401, Some ExecutionPhase.NotStarted
        | HostFailureReason.SessionForbidden ->
            HostFailureCode.SessionForbidden, 403, Some ExecutionPhase.NotStarted
        | HostFailureReason.MethodRejected ->
            HostFailureCode.MethodRejected, 405, Some ExecutionPhase.NotStarted
        | HostFailureReason.AntiforgeryRejected ->
            HostFailureCode.AntiforgeryRejected, 403, Some ExecutionPhase.NotStarted
        | HostFailureReason.EndpointMissing ->
            HostFailureCode.EndpointMissing, 404, Some ExecutionPhase.NotStarted
        | HostFailureReason.RateLimited ->
            HostFailureCode.RateLimited, 429, Some ExecutionPhase.NotStarted
        | HostFailureReason.InputInvalid ->
            HostFailureCode.InputInvalid, 400, Some ExecutionPhase.NotStarted
        | HostFailureReason.BeforeDispatchFailed ->
            HostFailureCode.Internal, 500, Some ExecutionPhase.NotStarted
        | HostFailureReason.DispatchUnconfirmed ->
            HostFailureCode.Internal, 500, Some ExecutionPhase.StartedUnconfirmed
        | HostFailureReason.CompletedResponseFailed -> HostFailureCode.ResponseFailed, 500, None

    let private reasons =
        [
            HostFailureReason.ContractMismatch
            HostFailureReason.ConnectionRejected
            HostFailureReason.OriginRejected
            HostFailureReason.MediaTypeRejected
            HostFailureReason.BodyTooLarge
            HostFailureReason.SessionRejected
            HostFailureReason.SessionForbidden
            HostFailureReason.MethodRejected
            HostFailureReason.AntiforgeryRejected
            HostFailureReason.EndpointMissing
            HostFailureReason.RateLimited
            HostFailureReason.InputInvalid
            HostFailureReason.BeforeDispatchFailed
            HostFailureReason.DispatchUnconfirmed
            HostFailureReason.CompletedResponseFailed
        ]

    let private codeKind reason = let code, _, _ = policy reason in code

    // Flat exhaustive code spelling; union coverage and token uniqueness use the shared mechanism.
    // fsharplint:disable-next-line CyclomaticComplexity
    let vocabulary =
        Vocabulary.define "host" (reasons |> List.map codeKind |> List.distinct) (function
            | HostFailureCode.ContractMismatch -> "CONTRACT_MISMATCH"
            | HostFailureCode.ConnectionRejected -> "CONNECTION_REJECTED"
            | HostFailureCode.OriginRejected -> "ORIGIN_REJECTED"
            | HostFailureCode.MediaTypeRejected -> "MEDIA_TYPE_REJECTED"
            | HostFailureCode.BodyTooLarge -> "BODY_TOO_LARGE"
            | HostFailureCode.SessionRejected -> "SESSION_REJECTED"
            | HostFailureCode.SessionForbidden -> "SESSION_FORBIDDEN"
            | HostFailureCode.MethodRejected -> "METHOD_REJECTED"
            | HostFailureCode.AntiforgeryRejected -> "ANTIFORGERY_REJECTED"
            | HostFailureCode.EndpointMissing -> "ENDPOINT_MISSING"
            | HostFailureCode.RateLimited -> "RATE_LIMITED"
            | HostFailureCode.InputInvalid -> "INPUT_INVALID"
            | HostFailureCode.Internal -> "INTERNAL"
            | HostFailureCode.ResponseFailed -> "RESPONSE_FAILED")

    // Two dispatch reasons share INTERNAL, distinguished by their phase in the wire contract.
    let private reasonVocabulary =
        Vocabulary.define "host-reason" reasons (fun reason ->
            let code, status, phase = policy reason
            Vocabulary.token vocabulary code + ":" + string status + ":" + string phase)

    let all = Vocabulary.all reasonVocabulary

    let code reason =
        codeKind reason |> Vocabulary.token vocabulary

    let status reason =
        let _, value, _ = policy reason in value

    let phase reason =
        let _, _, value = policy reason in value
