namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Json

[<RequireQualifiedAccess>]
type ReplyKind =
    | Succeeded
    | Reviewed
    | Accepted
    | AlreadyAccepted
    | NotAccepted
    | Rejected
    | Failed
    | OutcomeUnknown

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ReplyKind =
    let vocabulary =
        Vocabulary.ofUnion<ReplyKind> "replyKind" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

type ReplyClass = { Kind: ReplyKind; ExitCode: ExitCode }
type ClassifiedReply = { Wire: JsonValue; Class: ReplyClass }

module OutcomePresentation =
    let private classification kind exitCode = { Kind = kind; ExitCode = exitCode }

    let classify outcome =
        Outcome.fold
            (fun _ -> classification ReplyKind.Succeeded ExitCode.Success)
            (fun _ -> classification ReplyKind.Rejected ExitCode.Rejected)
            (fun _ -> classification ReplyKind.Failed ExitCode.Failed)
            outcome

    let recommendedAction outcome =
        Outcome.fold
            (fun _ -> RecommendedAction.NoneRequired)
            Refusals.action
            FaultAdvice.action
            outcome

    let classifyPreview (reply: PreviewReply) =
        match reply.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed _) ->
            classification ReplyKind.Reviewed ExitCode.Success
        | Outcome.Succeeded(Previewed.AlreadyAccepted _) ->
            classification ReplyKind.AlreadyAccepted ExitCode.Success
        | outcome -> classify outcome

    let classifyExecute =
        function
        | ExecuteResult.Accepted _ -> classification ReplyKind.Accepted ExitCode.Success
        | ExecuteResult.AlreadyAccepted _ ->
            classification ReplyKind.AlreadyAccepted ExitCode.Success
        | ExecuteResult.Rejected _ -> classification ReplyKind.Rejected ExitCode.Rejected
        | ExecuteResult.Failed _ -> classification ReplyKind.Failed ExitCode.Failed
        | ExecuteResult.OutcomeUnknown ->
            classification ReplyKind.OutcomeUnknown ExitCode.OutcomeUnknown

    let classifyResolve =
        function
        | Outcome.Succeeded(Resolved.Accepted _) ->
            classification ReplyKind.Accepted ExitCode.Success
        | Outcome.Succeeded Resolved.NotAccepted ->
            classification ReplyKind.NotAccepted ExitCode.Rejected
        | outcome -> classify outcome
