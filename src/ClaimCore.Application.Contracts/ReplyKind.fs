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
