namespace ClaimCore.Protocol

open ClaimCore.Protocol
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

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module HostFailureReason =
    // Reasons and wire codes are separate sets: dispatch reasons share INTERNAL and differ by phase.
    let vocabulary =
        Vocabulary.ofUnion<HostFailureReason> "host-reason" TokenCasing.ScreamingSnake None


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

    let private codeKind reason = let code, _, _ = policy reason in code

    let all = Vocabulary.all vocabulary

    let code reason =
        codeKind reason |> Vocabulary.token HostFailureCode.vocabulary

    let status reason =
        let _, value, _ = policy reason in value

    let phase reason =
        let _, _, value = policy reason in value
