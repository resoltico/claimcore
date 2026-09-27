namespace ClaimCore.Application

open System
open ClaimCore.Domain

type internal LifecycleApprovalEvidence =
    {
        ApprovalId: Guid
        ApproverId: Guid
        ExpiresAt: DateTimeOffset
    }

type internal LifecycleDecisionResult =
    {
        State: LifecycleState
        Snapshot: CaseView option
    }

/// Storage supplies independently read approval rows and historical-payment evidence; only
/// Application mints the opaque Domain proof values after those facts are checked under lock.
module internal CaseLifecycleDecisions =
    let restore caseId snapshot disposition privacy holds =
        CaseLifecycle.restoreProjection caseId snapshot disposition privacy holds

    let private reason =
        function
        | LifecycleMutation.VoidDataEntryError value
        | LifecycleMutation.ReinstateVoided value
        | LifecycleMutation.RequestErasure value
        | LifecycleMutation.MarkErasurePending value
        | LifecycleMutation.ReleaseHold(_, value) -> value
        | LifecycleMutation.PurgeLivePayload(value, _) -> value
        | LifecycleMutation.RecordHold(_, ground, _) -> ground

    let private decision
        caseId
        (change: LifecycleChange)
        actorId
        eventDigest
        (approvals: LifecycleApprovalEvidence list)
        instant
        expectedAction
        =
        {
            Action = expectedAction
            OperationId = change.EventId
            CaseId = caseId
            ExpectedRevision = change.ExpectedRevision
            EventDigest = eventDigest
            ActorId = actorId
            At = instant
            Reason = reason change.Action
            Approvals =
                approvals
                |> List.map (fun approval ->
                    LifecycleEvidenceAuthority.approval
                        expectedAction
                        change.EventId
                        caseId
                        change.ExpectedRevision
                        eventDigest
                        approval.ApproverId
                        approval.ExpiresAt)
        }

    let private withSnapshot
        (transition: Result<DispositionTransition, LifecycleRefusal>)
        : Result<LifecycleDecisionResult, LifecycleRefusal> =
        transition
        |> Result.map (fun value ->
            {
                State = value.State
                Snapshot = Some value.Snapshot
            })

    let private pending caseId state actorId instant reasonValue witnessedRequest =
        match witnessedRequest with
        | None -> Error LifecycleRefusal.ErasureEvidenceIncomplete
        | Some(ticket, digest) ->
            match LifecycleEvidenceAuthority.witnessedFence caseId ticket digest with
            | None -> Error LifecycleRefusal.ErasureEvidenceIncomplete
            | Some fence ->
                CaseLifecycle.markErasurePending state fence actorId instant reasonValue
                |> Result.map (fun next -> { State = next; Snapshot = None })

    let private purge state selected (validUntil: DateTimeOffset) instant =
        if
            validUntil.Offset <> TimeSpan.Zero
            || validUntil <= instant
            || validUntil - instant > TimeSpan.FromHours 24.0
        then
            Error LifecycleRefusal.InvalidTime
        else
            CaseLifecycle.authorizeLivePurge state (selected LifecycleAction.PurgeLivePayload)
            |> Result.map (fun () -> { State = state; Snapshot = None })

    let decide
        caseId
        (snapshot: CaseView)
        (state: LifecycleState)
        (change: LifecycleChange)
        actorId
        eventDigest
        historicalPayment
        (witnessedRequest: (Guid * string) option)
        (approvals: LifecycleApprovalEvidence list)
        instant
        =
        let selected action =
            decision caseId change actorId eventDigest approvals instant action

        match change.Action with
        | LifecycleMutation.VoidDataEntryError _ ->
            let paymentEvidence =
                if historicalPayment then
                    LifecycleEvidenceAuthority.historicalPaymentAssertion
                else
                    LifecycleEvidenceAuthority.noHistoricalPaymentAssertionVerified

            CaseLifecycle.voidDataEntryError
                state
                snapshot
                paymentEvidence
                (selected LifecycleAction.VoidDataEntryError)
            |> withSnapshot
        | LifecycleMutation.ReinstateVoided _ ->
            CaseLifecycle.reinstateVoided state snapshot (selected LifecycleAction.ReinstateVoided)
            |> withSnapshot
        | LifecycleMutation.RequestErasure reasonValue ->
            CaseLifecycle.requestErasure state actorId instant reasonValue
            |> Result.map (fun next -> { State = next; Snapshot = None })
        | LifecycleMutation.MarkErasurePending reasonValue ->
            pending caseId state actorId instant reasonValue witnessedRequest
        | LifecycleMutation.PurgeLivePayload(_, validUntil) ->
            purge state selected validUntil instant
        | LifecycleMutation.RecordHold(holdId, ground, reviewOn) ->
            CaseLifecycle.recordHold
                state
                {
                    Id = holdId
                    Ground = ground
                    ReviewOn = reviewOn
                    RecordedBy = actorId
                    RecordedAt = instant
                }
            |> Result.map (fun next -> { State = next; Snapshot = None })
        | LifecycleMutation.ReleaseHold(holdId, reasonValue) ->
            CaseLifecycle.releaseHold state holdId actorId instant reasonValue
            |> Result.map (fun next -> { State = next; Snapshot = None })

    let authorizeOwnerPurge
        caseId
        (state: LifecycleState)
        (change: LifecycleChange)
        eventDigest
        (approvals: LifecycleApprovalEvidence list)
        instant
        =
        match change.Action with
        | LifecycleMutation.PurgeLivePayload(reasonValue, validUntil) when
            validUntil.Offset = TimeSpan.Zero
            && validUntil > instant
            && validUntil - instant <= TimeSpan.FromHours 24.0
            ->
            let decision: OwnerPurgeDecision =
                {
                    OperationId = change.EventId
                    CaseId = caseId
                    ExpectedRevision = change.ExpectedRevision
                    EventDigest = eventDigest
                    At = instant
                    Reason = reasonValue
                    Approvals =
                        approvals
                        |> List.map (fun approval ->
                            LifecycleEvidenceAuthority.approval
                                LifecycleAction.PurgeLivePayload
                                change.EventId
                                caseId
                                change.ExpectedRevision
                                eventDigest
                                approval.ApproverId
                                approval.ExpiresAt)
                }

            CaseLifecycle.authorizeOwnerLivePurge state decision
        | LifecycleMutation.PurgeLivePayload _ -> Error LifecycleRefusal.InvalidTime
        | _ -> Error LifecycleRefusal.WrongPrivacyPhase
