namespace ClaimCore.ContractGeneration

open System
open ClaimCore.Application
open ClaimCore.Contracts

module internal WebRealDataActivationCorpusSamples =
    let private approvalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")
    let private digest = Array.create 32 0x11uy

    let private review =
        {
            PlanId = Guid.Parse("10000000-0000-4000-8000-000000000001")
            ActivationId = Guid.Parse("10000000-0000-4000-8000-000000000002")
            InstallationId = Guid.Parse("10000000-0000-4000-8000-000000000003")
            LineageId = Guid.Parse("10000000-0000-4000-8000-000000000004")
            Epoch = 1L
            WriterGeneration = 1L
            PolicySha256 = digest
            PublicationRootSha256 = digest
            CycleId = Guid.Parse("10000000-0000-4000-8000-000000000005")
            LeaseId = Guid.Parse("10000000-0000-4000-8000-000000000006")
            CaptureReceiptSha256 = digest
            PrimaryBaseCopyId = Guid.Parse("10000000-0000-4000-8000-000000000007")
            WitnessBaseCopyId = Guid.Parse("10000000-0000-4000-8000-000000000008")
            PrimaryBasePhysicalReceiptSha256 = digest
            WitnessBasePhysicalReceiptSha256 = digest
            CheckpointObjectSha256 = digest
            TestRestoreReportSha256 = digest
            TestRestoreFullAuditSha256 = digest
            MinimumArtifactCutoffSequence = 1L
            MinimumPrimaryWalHorizon = "0/10"
            MinimumWitnessWalHorizon = "0/10"
            CanonicalPlan = Text.Encoding.ASCII.GetBytes("synthetic-plan")
            PlanSha256 = digest
            PublishedAt = DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero)
            PublicationWitnessSequence = 1L
            PublicationWitnessHash = digest
            ApprovalExpiresNoLaterThan = DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero)
        }

    let private approval id outcome =
        WebCorpusSamples.sample
            id
            "authority.approveRealDataActivation"
            (WebWireCodec.realDataActivationApproval outcome)

    let all =
        [
            WebCorpusSamples.sample
                "real-data-activation-reviewed"
                "authority.reviewRealDataActivation"
                (WebWireCodec.realDataActivationReview (
                    RealDataActivationPlanReviewOutcome.Reviewed review
                ))
            WebCorpusSamples.sample
                "real-data-activation-review-unavailable"
                "authority.reviewRealDataActivation"
                (WebWireCodec.realDataActivationReview
                    RealDataActivationPlanReviewOutcome.ResourceUnavailable)
            approval
                "real-data-activation-approved"
                (RealDataActivationApprovalOutcome.Approved(approvalId, 3L))
            approval
                "real-data-activation-approval-unavailable"
                RealDataActivationApprovalOutcome.ResourceUnavailable
            approval
                "real-data-activation-approval-unconfirmed"
                (RealDataActivationApprovalOutcome.StartedUnconfirmed approvalId)
        ]
