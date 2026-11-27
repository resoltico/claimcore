namespace ClaimCore.Application.Contracts

open System
open ClaimCore.Domain

/// Confirmed operational deletion. The request ID identifies the browser command, not a ledger entry.
type SessionTermination = { Count: int64; Self: bool }

type OwnSessionTerminationRequest =
    {
        OperationId: OperationId
        SettleOnly: bool option
        ConfirmRetirement: bool option
    }

type SessionTerminationScope =
    {
        InstallationId: Guid
        RequesterKey: string
        ObservedAt: DateTimeOffset
        RetirementCutoff: DateTimeOffset option
    }

type PersonSessionTerminationRequest =
    {
        ActorId: Guid
        OperationId: OperationId
        SettleOnly: bool option
        ConfirmRetirement: bool option
    }

[<RequireQualifiedAccess>]
type OwnSessionScope =
    | Principal
    | Account of ActorId

module OwnSessionScope =
    let select current credentialUsable =
        match current with
        | None -> Some OwnSessionScope.Principal
        | Some actor when credentialUsable -> Some(OwnSessionScope.Account actor)
        | Some _ -> None

[<RequireQualifiedAccess>]
type SessionTerminationResult =
    | Succeeded of SessionTermination
    | RetirementConfirmed
    | Rejected of Refusal
    | Failed of CoreFault
    | OutcomeUnknown

module SessionTerminationResult =
    let classify =
        function
        | SessionTerminationResult.Succeeded _
        | SessionTerminationResult.RetirementConfirmed -> ReplyKind.Succeeded
        | SessionTerminationResult.Rejected _ -> ReplyKind.Rejected
        | SessionTerminationResult.Failed _ -> ReplyKind.Failed
        | SessionTerminationResult.OutcomeUnknown -> ReplyKind.OutcomeUnknown
