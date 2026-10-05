namespace ClaimCore.Postgres

open System
open System.Data
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Witness

/// Read-only, actor-bound presentation of the exact published nonclaimant activation plan.
module internal RealDataActivationPlanReview =
    let private project (published: PublishedInstallationUsePlan) (now: DateTimeOffset) =
        let plan = published.Plan

        {
            PlanId = published.PlanId
            ActivationId = published.ActivationId
            InstallationId = plan.InstallationId
            LineageId = plan.LineageId
            Epoch = plan.Epoch
            WriterGeneration = plan.WriterGeneration
            PolicySha256 = Convert.FromHexString plan.PolicySha256
            PublicationRootSha256 = Convert.FromHexString plan.PublicationRootSha256
            CycleId = plan.CycleId
            LeaseId = plan.LeaseId
            CaptureReceiptSha256 = Convert.FromHexString plan.CaptureReceiptSha256
            PrimaryBaseCopyId = plan.PrimaryBaseCopyId
            WitnessBaseCopyId = plan.WitnessBaseCopyId
            PrimaryBasePhysicalReceiptSha256 =
                Convert.FromHexString plan.PrimaryBasePhysicalReceiptSha256
            WitnessBasePhysicalReceiptSha256 =
                Convert.FromHexString plan.WitnessBasePhysicalReceiptSha256
            CheckpointObjectSha256 = Convert.FromHexString plan.CheckpointObjectSha256
            TestRestoreReportSha256 = Convert.FromHexString plan.TestRestoreReportSha256
            TestRestoreFullAuditSha256 = Convert.FromHexString plan.TestRestoreFullAuditSha256
            MinimumArtifactCutoffSequence = plan.MinimumArtifactCutoffSequence
            MinimumPrimaryWalHorizon = plan.MinimumPrimaryWalHorizon
            MinimumWitnessWalHorizon = plan.MinimumWitnessWalHorizon
            CanonicalPlan = Array.copy plan.Canonical
            PlanSha256 = Convert.FromHexString plan.PlanSha256
            PublishedAt = published.PublishedAt
            PublicationWitnessSequence = published.SettlementSequence
            PublicationWitnessHash = Array.copy published.SettlementHash
            ApprovalExpiresNoLaterThan = now.AddHours(24.)
        }

    let private eligible (snapshot: Snapshot) (published: PublishedInstallationUsePlan) =
        let plan = published.Plan

        snapshot.Identity.InstallationId = plan.InstallationId
        && snapshot.Identity.LineageId = plan.LineageId
        && snapshot.Identity.Epoch = plan.Epoch
        && snapshot.WriterGeneration = plan.WriterGeneration
        && snapshot.Use.Scope = InstallationUseScope.RealData
        && snapshot.Use.Phase = InstallationUsePhase.BootstrapNoCases
        && not snapshot.HandoffPending
        && not snapshot.ActivationPending

    let private underLock
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        planId
        ct
        =
        task {
            let! revision =
                ActorGrantRead.lockRevision connection transaction true CancellationToken.None

            let! authority =
                ActorGrantRead.loadUnderLock
                    connection
                    transaction
                    context.Binding.Principal
                    ResourceScope.Installation
                    revision
                    ct

            match authority with
            | Some live when
                RealDataActivationOwnerPolicy.current
                    context
                    live
                    revision
                    EndpointAction.ReviewRealDataActivation
                ->
                let! published =
                    InstallationUsePlanRead.verified connection transaction witness planId ct

                let! snapshot = witness.Snapshot(ct)

                match published with
                | Some plan when eligible snapshot plan ->
                    let! now = Sql.databaseNow connection transaction ct
                    return RealDataActivationPlanReviewOutcome.Reviewed(project plan now)
                | _ -> return RealDataActivationPlanReviewOutcome.ResourceUnavailable
            | _ -> return RealDataActivationPlanReviewOutcome.ResourceUnavailable
        }

    let review dataSource (witness: WitnessProtocol) (context: ActorCallContext) planId ct =
        task {
            if
                context.Action <> EndpointAction.ReviewRealDataActivation
                || context.CaseId.IsSome
                || not (PrincipalKey.isHuman context.Binding.Principal)
                || planId = Guid.Empty
            then
                return RealDataActivationPlanReviewOutcome.ResourceUnavailable
            else
                try
                    do! witness.Admit(ct)

                    use! connection =
                        RuntimeDatabase.openConnectionAsyncWithCancellation dataSource ct

                    use! _authorityLease =
                        AuthorityOperationFence.acquireShared (Some dataSource) connection ct

                    use! transaction =
                        connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)

                    return! underLock connection transaction witness context planId ct
                with _ ->
                    return RealDataActivationPlanReviewOutcome.ResourceUnavailable
        }
