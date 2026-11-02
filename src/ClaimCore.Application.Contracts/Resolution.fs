namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type Resolved =
    | Accepted of OperationReceipt
    | NotAccepted

/// A passive observation never establishes a new promise of nonacceptance.
[<RequireQualifiedAccess>]
type OperationStatus =
    | Accepted of OperationReceipt
    | NotAccepted
    | Unresolved
