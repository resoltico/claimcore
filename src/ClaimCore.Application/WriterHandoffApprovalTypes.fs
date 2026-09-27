namespace ClaimCore.Application

open System

/// One human installation owner's witnessed approval of an exact writer handoff fence.
[<NoEquality; NoComparison>]
type WriterHandoffApprovalRequest =
    {
        ApprovalId: Guid
        HandoffId: Guid
        OldGeneration: int64
        ExpectedWitnessSequence: int64
        ExpectedWitnessHash: byte array
        NewCapabilitySha256: byte array
        CheckpointSigningKeyId: Guid
        FenceReportSha256: byte array
        InventorySha256: byte array
        ExpiresAt: DateTimeOffset
    }

[<RequireQualifiedAccess>]
type WriterHandoffApprovalOutcome =
    | Approved of approvalId: Guid * authorityRevision: int64
    | ResourceUnavailable
    | StartedUnconfirmed of approvalId: Guid
