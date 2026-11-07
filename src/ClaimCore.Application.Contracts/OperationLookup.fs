namespace ClaimCore.Application.Contracts

open System

/// PENDING/NOT_ACCEPTED describe the requester's account, never an undisclosed foreign acceptance.
[<RequireQualifiedAccess>]
type OperationLookupResult =
    | Accepted of receipt: OperationReceipt * byRequester: bool
    | Pending of finalAt: DateTimeOffset
    | NotAccepted
