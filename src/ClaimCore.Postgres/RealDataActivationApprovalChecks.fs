namespace ClaimCore.Postgres

open System
open System.Threading
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Witness
open WitnessProtocolReconciliation

module internal RealDataActivationApprovalChecks =
    let private digest32 (value: byte array) =
        not (isNull (box value)) && value.Length = 32

    let valid (context: ActorCallContext) (request: RealDataActivationApprovalRequest) =
        context.Action = EndpointAction.ApproveRealDataActivation
        && context.CaseId.IsNone
        && PrincipalKey.isHuman context.Binding.Principal
        && request.ApprovalId <> Guid.Empty
        && request.PlanId <> Guid.Empty
        && request.ActivationId <> Guid.Empty
        && request.InstallationId <> Guid.Empty
        && request.LineageId <> Guid.Empty
        && request.Epoch > 0L
        && request.WriterGeneration > 0L
        && request.ReviewWitnessSequence >= 0L
        && request.ExpectedWitnessSequence >= request.ReviewWitnessSequence
        && digest32 request.ActivationPlanSha256
        && digest32 request.PolicySha256
        && digest32 request.ReviewWitnessHash
        && digest32 request.ExpectedWitnessHash
        && Sql.isUtcMicrosecond request.ExpiresAt
        && request.ActivationId =
            InstallationUseActivationCandidate.eventIdFromPlan
                request.InstallationId
                request.ActivationPlanSha256
        && request.PlanId =
            InstallationUseActivationCandidate.planIdFromDigest
                request.InstallationId
                request.ActivationPlanSha256

    let matchesPublished
        (request: RealDataActivationApprovalRequest)
        (published: PublishedInstallationUsePlan)
        =
        let plan = published.Plan

        request.PlanId = published.PlanId
        && request.ActivationId = published.ActivationId
        && request.InstallationId = plan.InstallationId
        && request.LineageId = plan.LineageId
        && request.Epoch = plan.Epoch
        && request.WriterGeneration = plan.WriterGeneration
        && request.ActivationPlanSha256 = Convert.FromHexString(plan.PlanSha256)
        && request.PolicySha256 = Convert.FromHexString(plan.PolicySha256)

    let chain
        (witness: WitnessProtocol)
        (request: RealDataActivationApprovalRequest)
        actorId
        now
        requireUnexpired
        first
        (ct: CancellationToken)
        =
        task {
            match first with
            | None ->
                return
                    request.ExpectedWitnessSequence = request.ReviewWitnessSequence
                    && request.ExpectedWitnessHash = request.ReviewWitnessHash
            | Some(first: FirstRealDataActivationApproval) ->
                let matching =
                    first.ApproverActorId <> actorId
                    && first.PlanSha256 = request.ActivationPlanSha256
                    && first.ReviewSequence = request.ReviewWitnessSequence
                    && first.ReviewHash = request.ReviewWitnessHash
                    && first.ExpectedSequence = request.ReviewWitnessSequence
                    && first.ExpectedHash = request.ReviewWitnessHash
                    && (not requireUnexpired || first.ExpiresAt > now)
                    && first.WitnessSequence + 1L = request.ExpectedWitnessSequence

                if not matching then
                    return false
                else
                    do!
                        witness.VerifyAuthorityEvidenceForInstallation(
                            first.ApprovalId,
                            first.WitnessSequence,
                            first.WitnessEpoch,
                            first.WitnessHash,
                            first.CandidateHash,
                            ct
                        )

                    return true
        }

    let matchesSnapshot (request: RealDataActivationApprovalRequest) (snapshot: Snapshot) =
        let identity =
            snapshot.Identity.InstallationId = request.InstallationId
            && snapshot.Identity.LineageId = request.LineageId
            && snapshot.Identity.Epoch = request.Epoch

        let bootstrap =
            snapshot.WriterGeneration = request.WriterGeneration
            && snapshot.Use.Scope = InstallationUseScope.RealData
            && snapshot.Use.Phase = InstallationUsePhase.BootstrapNoCases
            && not snapshot.ActivationPending
            && not snapshot.HandoffPending

        identity && bootstrap

    let freshTip (request: RealDataActivationApprovalRequest) (snapshot: Snapshot) =
        snapshot.TipSequence = request.ExpectedWitnessSequence
        && snapshot.TipHash = request.ExpectedWitnessHash

    let pendingTip (snapshot: Snapshot) (pending: PendingRealDataActivationApproval) =
        snapshot.TipSequence = pending.Intent.Ticket.Sequence
        && snapshot.TipHash = pending.Intent.Ticket.EntryHash

    let pendingValid
        (request: RealDataActivationApprovalRequest)
        (snapshot: Snapshot)
        (pending: PendingRealDataActivationApproval)
        (now: DateTimeOffset)
        =
        pendingTip snapshot pending
        && pending.ApprovedAt <= now
        && pending.ApprovedAt < request.ExpiresAt
        && request.ExpiresAt <= pending.ApprovedAt.AddHours(24.)

    let historicalReview
        (witness: WitnessProtocol)
        (request: RealDataActivationApprovalRequest)
        ct
        =
        task {
            try
                do!
                    witness.VerifyHistoricalTip(
                        request.ReviewWitnessSequence,
                        request.ReviewWitnessHash,
                        ct
                    )

                return true
            with
            | :? OperationCanceledException -> return raise (OperationCanceledException(ct))
            | _ -> return false
        }

    let baseEligible witness request actorId now requireUnexpired first snapshot ct =
        task {
            let! historical = historicalReview witness request ct

            if not historical || not (matchesSnapshot request snapshot) then
                return false
            else
                return! chain witness request actorId now requireUnexpired first ct
        }
