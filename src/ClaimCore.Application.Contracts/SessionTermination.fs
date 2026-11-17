namespace ClaimCore.Application.Contracts

open System
open ClaimCore.Domain

/// Confirmed operational deletion, independent of ledger operation identities.
type SessionTermination = { Count: int64; Self: bool }
type PersonSessionTerminationRequest = { ActorId: Guid }

[<RequireQualifiedAccess>]
type SessionTerminationResult =
    | Succeeded of SessionTermination
    | Rejected of Refusal
    | Failed of CoreFault
    | OutcomeUnknown

module SessionTerminationResult =
    let classify =
        function
        | SessionTerminationResult.Succeeded _ -> ReplyKind.Succeeded
        | SessionTerminationResult.Rejected _ -> ReplyKind.Rejected
        | SessionTerminationResult.Failed _ -> ReplyKind.Failed
        | SessionTerminationResult.OutcomeUnknown -> ReplyKind.OutcomeUnknown
