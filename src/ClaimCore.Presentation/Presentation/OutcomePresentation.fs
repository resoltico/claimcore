namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

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

    let classifyPreview (reply: PresentedPreviewReply) =
        match reply.Outcome with
        | Outcome.Succeeded(PresentedPreviewed.Reviewed _) ->
            classification ReplyKind.Reviewed ExitCode.Success
        | Outcome.Succeeded(PresentedPreviewed.AlreadyAccepted _) ->
            classification ReplyKind.AlreadyAccepted ExitCode.Success
        | outcome -> classify outcome

    let classifyExecute =
        function
        | PresentedExecuteResult.Accepted _ -> classification ReplyKind.Accepted ExitCode.Success
        | PresentedExecuteResult.AlreadyAccepted _ ->
            classification ReplyKind.AlreadyAccepted ExitCode.Success
        | PresentedExecuteResult.Rejected _ -> classification ReplyKind.Rejected ExitCode.Rejected
        | PresentedExecuteResult.Failed _ -> classification ReplyKind.Failed ExitCode.Failed
        | PresentedExecuteResult.OutcomeUnknown ->
            classification ReplyKind.OutcomeUnknown ExitCode.OutcomeUnknown

    let classifyOperationGet =
        function
        | Outcome.Succeeded(PresentedLookupResult.Accepted _) ->
            classification ReplyKind.Accepted ExitCode.Success
        | Outcome.Succeeded(PresentedLookupResult.Pending _) ->
            classification ReplyKind.Pending ExitCode.OutcomeUnknown
        | Outcome.Succeeded PresentedLookupResult.NotAccepted ->
            classification ReplyKind.NotAccepted ExitCode.Rejected
        | outcome -> classify outcome
