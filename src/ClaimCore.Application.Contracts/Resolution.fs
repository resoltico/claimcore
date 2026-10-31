namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type Resolved =
    | Accepted of OperationReceipt
    | NotAccepted
