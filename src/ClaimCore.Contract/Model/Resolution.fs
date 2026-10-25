namespace ClaimCore.Contract

open System

type ResolveRequest = { OperationId: Guid }

[<RequireQualifiedAccess>]
type ResolveReply =
    | Accepted of OperationReceipt
    | NotAccepted
    | Rejected of Refusal
    | Failed of CoreFault
