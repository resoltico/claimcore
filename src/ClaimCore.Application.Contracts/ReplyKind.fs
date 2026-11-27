namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type ReplyKind =
    | Succeeded
    | Reviewed
    | Accepted
    | AlreadyAccepted
    | NotAccepted
    | Pending
    | Rejected
    | Failed
    | OutcomeUnknown

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ReplyKind =
    let vocabulary =
        Vocabulary.ofUnion<ReplyKind> "replyKind" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary


/// What an admitted reply proves about the attempted operation.
[<RequireQualifiedAccess>]
type OperationSettlement =
    | Recorded
    | NotRecorded
    | Pending
    | Refused
    | Unresolved

module OperationSettlement =
    let vocabulary =
        Vocabulary.ofUnion<OperationSettlement>
            "operationSettlement"
            TokenCasing.ScreamingSnake
            None

    let token = Vocabulary.token vocabulary

    // A fault is no evidence about an earlier dispatch of the same operation identity.
    let ofReply =
        function
        | ReplyKind.Accepted
        | ReplyKind.AlreadyAccepted -> OperationSettlement.Recorded
        | ReplyKind.NotAccepted -> OperationSettlement.NotRecorded
        | ReplyKind.Pending -> OperationSettlement.Pending
        | ReplyKind.Rejected -> OperationSettlement.Refused
        | ReplyKind.Succeeded
        | ReplyKind.Reviewed
        | ReplyKind.Failed
        | ReplyKind.OutcomeUnknown -> OperationSettlement.Unresolved

    let isFinal =
        function
        | OperationSettlement.Recorded
        | OperationSettlement.NotRecorded -> true
        | OperationSettlement.Pending
        | OperationSettlement.Refused
        | OperationSettlement.Unresolved -> false
