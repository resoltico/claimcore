namespace ClaimCore.Wire

[<RequireQualifiedAccess>]
type ExecutionPhase =
    | NotStarted
    | StartedUnconfirmed

[<RequireQualifiedAccess>]
type OutcomeTag =
    | Succeeded
    | Reviewed
    | Accepted
    | AlreadyAccepted
    | Rejected
    | Failed
    | Cancelled
    | OutcomeUnknown

[<RequireQualifiedAccess>]
type AttemptKnowledge =
    | NotSent
    | ReadSucceeded
    | ReadFailed
    | Accepted
    | NotAccepted
    | Unconfirmed

[<RequireQualifiedAccess>]
type AccumulatedKnowledge =
    | Known of AttemptKnowledge
    | ContentExcluded

[<RequireQualifiedAccess>]
type ResolutionEvidence =
    | NoEvidence
    | ContentAccepted
    | ContentExcluded

[<RequireQualifiedAccess>]
type AttemptResult =
    | NotDispatched
    | DispatchedWithoutReply
    | HostFailure of ExecutionPhase option
    | Outcome of OutcomeTag * diagnostic: string option

module Delivery =
    let private outcome mutates =
        function
        | OutcomeTag.Accepted
        | OutcomeTag.AlreadyAccepted -> AttemptKnowledge.Accepted
        | OutcomeTag.Succeeded
        | OutcomeTag.Reviewed -> AttemptKnowledge.ReadSucceeded
        | OutcomeTag.Rejected
        | OutcomeTag.Failed
        | OutcomeTag.Cancelled ->
            if mutates then
                AttemptKnowledge.NotAccepted
            else
                AttemptKnowledge.ReadFailed
        | OutcomeTag.OutcomeUnknown -> AttemptKnowledge.Unconfirmed

    let knowledge endpoint =
        function
        | AttemptResult.NotDispatched -> AttemptKnowledge.NotSent
        | AttemptResult.DispatchedWithoutReply ->
            if Endpoint.mutates endpoint then
                AttemptKnowledge.Unconfirmed
            else
                AttemptKnowledge.ReadFailed
        | AttemptResult.HostFailure phase ->
            if not (Endpoint.mutates endpoint) then
                AttemptKnowledge.ReadFailed
            elif phase = Some ExecutionPhase.NotStarted then
                AttemptKnowledge.NotAccepted
            else
                AttemptKnowledge.Unconfirmed
        | AttemptResult.Outcome(tag, _) -> outcome (Endpoint.mutates endpoint) tag

    let evidence endpoint attempt =
        if Endpoint.receiptScope endpoint = None then
            ResolutionEvidence.NoEvidence
        elif knowledge endpoint attempt = AttemptKnowledge.Accepted then
            ResolutionEvidence.ContentAccepted
        else
            match attempt with
            | AttemptResult.Outcome(OutcomeTag.Rejected, Some "OPERATION_CONTENT_CONFLICT") ->
                ResolutionEvidence.ContentExcluded
            | _ -> ResolutionEvidence.NoEvidence

    let accumulate prior attempt =
        function
        | ResolutionEvidence.ContentAccepted -> AccumulatedKnowledge.Known AttemptKnowledge.Accepted
        | ResolutionEvidence.ContentExcluded -> AccumulatedKnowledge.ContentExcluded
        | ResolutionEvidence.NoEvidence ->
            if prior = AccumulatedKnowledge.Known AttemptKnowledge.Unconfirmed then
                prior
            else
                AccumulatedKnowledge.Known attempt
