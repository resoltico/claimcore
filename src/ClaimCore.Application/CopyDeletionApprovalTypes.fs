namespace ClaimCore.Application

open System

/// A human verifier's metadata-only approval of one exact managed-copy deletion report.
[<NoEquality; NoComparison>]
type CopyDeletionApprovalRequest =
    {
        ApprovalId: Guid
        DeletionEventId: Guid
        CopyId: Guid
        VerifierSigningKeyId: Guid
        ExpectedCopyRevision: int64
        LocationCommitment: byte array
        InspectionReportSha256: byte array
        WitnessCutoffSequence: int64
        WitnessCutoffHash: byte array
        ExpiresAt: DateTimeOffset
    }

[<RequireQualifiedAccess>]
type CopyDeletionApprovalOutcome =
    | Approved of approvalId: Guid * authorityRevision: int64
    | ResourceUnavailable
    | StartedUnconfirmed of approvalId: Guid
