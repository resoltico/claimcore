namespace ClaimCore.Contract

open ClaimCore.Domain

open System


[<RequireQualifiedAccess>]
type ResolveReply =
    | Accepted of OperationReceipt
    | NotAccepted
    | Rejected of Refusal
    | Failed of CoreFault
