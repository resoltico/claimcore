namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

type ClassifiedReply = { Wire: JsonValue; Class: ReplyKind }

module OutcomePresentation =
    let classify outcome =
        Outcome.fold
            (fun _ -> ReplyKind.Succeeded)
            (fun _ -> ReplyKind.Rejected)
            (fun _ -> ReplyKind.Failed)
            outcome

    let recommendedAction outcome =
        Outcome.fold
            (fun _ -> RecommendedAction.NoneRequired)
            Refusals.action
            FaultAdvice.action
            outcome

    let classifyPreview (reply: PreviewReply) =
        match reply.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed _) -> ReplyKind.Reviewed
        | Outcome.Succeeded(Previewed.AlreadyAccepted _) -> ReplyKind.AlreadyAccepted
        | outcome -> classify outcome

    let classifyExecute =
        function
        | ExecuteResult.Accepted _ -> ReplyKind.Accepted
        | ExecuteResult.AlreadyAccepted _ -> ReplyKind.AlreadyAccepted
        | ExecuteResult.Rejected _ -> ReplyKind.Rejected
        | ExecuteResult.Failed _ -> ReplyKind.Failed
        | ExecuteResult.ReceiptUnavailable -> ReplyKind.ReceiptUnavailable
        | ExecuteResult.OutcomeUnknown -> ReplyKind.OutcomeUnknown

    let classifyOperationGet =
        function
        | Outcome.Succeeded(OperationLookupResult.Accepted _) -> ReplyKind.Accepted
        | Outcome.Succeeded(OperationLookupResult.Pending _) -> ReplyKind.Pending
        | Outcome.Succeeded(OperationLookupResult.NotAccepted) -> ReplyKind.NotAccepted
        | outcome -> classify outcome
