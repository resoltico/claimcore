namespace ClaimCore.Application

open System

/// One authenticated human installation owner's witnessed approval of a stable activation plan.
[<NoEquality; NoComparison>]
type RealDataActivationApprovalRequest =
    {
        ApprovalId: Guid
        PlanId: Guid
        ActivationId: Guid
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        WriterGeneration: int64
        ActivationPlanSha256: byte array
        PolicySha256: byte array
        ReviewWitnessSequence: int64
        ReviewWitnessHash: byte array
        ExpectedWitnessSequence: int64
        ExpectedWitnessHash: byte array
        ExpiresAt: DateTimeOffset
    }

[<RequireQualifiedAccess>]
type RealDataActivationApprovalOutcome =
    | Approved of approvalId: Guid * authorityRevision: int64
    | ResourceUnavailable
    | StartedUnconfirmed of approvalId: Guid

/// Nonclaimant facts from the exact witnessed plan that an owner is asked to approve.
[<NoEquality; NoComparison>]
type RealDataActivationPlanReview =
    {
        PlanId: Guid
        ActivationId: Guid
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        WriterGeneration: int64
        PolicySha256: byte array
        PublicationRootSha256: byte array
        CycleId: Guid
        LeaseId: Guid
        CaptureReceiptSha256: byte array
        PrimaryBaseCopyId: Guid
        WitnessBaseCopyId: Guid
        PrimaryBasePhysicalReceiptSha256: byte array
        WitnessBasePhysicalReceiptSha256: byte array
        CheckpointObjectSha256: byte array
        TestRestoreReportSha256: byte array
        TestRestoreFullAuditSha256: byte array
        MinimumArtifactCutoffSequence: int64
        MinimumPrimaryWalHorizon: string
        MinimumWitnessWalHorizon: string
        CanonicalPlan: byte array
        PlanSha256: byte array
        PublishedAt: DateTimeOffset
        PublicationWitnessSequence: int64
        PublicationWitnessHash: byte array
        ApprovalExpiresNoLaterThan: DateTimeOffset
    }

[<NoEquality; NoComparison; RequireQualifiedAccess>]
type RealDataActivationPlanReviewOutcome =
    | Reviewed of RealDataActivationPlanReview
    | ResourceUnavailable
