namespace ClaimCore.Contract

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

module DeliveryTokens =
    let phases =
        [
            ExecutionPhase.NotStarted, "NOT_STARTED"
            ExecutionPhase.StartedUnconfirmed, "STARTED_UNCONFIRMED"
        ]

    let outcomes =
        [
            OutcomeTag.Succeeded, "SUCCEEDED"
            OutcomeTag.Reviewed, "REVIEWED"
            OutcomeTag.Accepted, "ACCEPTED"
            OutcomeTag.AlreadyAccepted, "ALREADY_ACCEPTED"
            OutcomeTag.Rejected, "REJECTED"
            OutcomeTag.Failed, "FAILED"
            OutcomeTag.Cancelled, "CANCELLED"
            OutcomeTag.OutcomeUnknown, "OUTCOME_UNKNOWN"
        ]

    let knowledgeDefinitions =
        [
            AttemptKnowledge.NotSent, "NOT_SENT"
            AttemptKnowledge.ReadSucceeded, "READ_SUCCEEDED"
            AttemptKnowledge.ReadFailed, "READ_FAILED"
            AttemptKnowledge.Accepted, "ACCEPTED"
            AttemptKnowledge.NotAccepted, "NOT_ACCEPTED"
            AttemptKnowledge.Unconfirmed, "UNCONFIRMED"
        ]

    let evidenceDefinitions =
        [
            ResolutionEvidence.NoEvidence, "NONE"
            ResolutionEvidence.ContentAccepted, "CONTENT_ACCEPTED"
            ResolutionEvidence.ContentExcluded, "CONTENT_EXCLUDED"
        ]

    let private token definitions value =
        definitions |> List.find (fst >> (=) value) |> snd

    let phase value = token phases value
    let outcome value = token outcomes value
