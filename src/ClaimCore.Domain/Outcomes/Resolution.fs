namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type Resolved =
    | Accepted of OperationReceipt
    | NotAccepted
