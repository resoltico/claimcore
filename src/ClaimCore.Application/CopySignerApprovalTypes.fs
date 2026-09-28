namespace ClaimCore.Application

open System

/// A human's exact, retryable approval of a backup-signing key change.
/// Actor identity is supplied only by the authenticated service binding.
[<RequireQualifiedAccess>]
type CopySignerAction =
    | Register
    | Retire

[<RequireQualifiedAccess>]
type CopySignerApprovalRole =
    | Owner of holderApprovalId: Guid
    | Custodian

[<RequireQualifiedAccess>]
type CopySignerPurpose =
    | CopyAttestor
    | LocationRegistry
    | LocationInspector
    | DeletionVerifier
    | RestoreReport
    | Checkpoint
    | WriterHandoffAbort
    | RestoreCopyVerifier

[<NoEquality; NoComparison>]
type CopySignerApprovalRequest =
    {
        ApprovalId: Guid
        SigningKeyId: Guid
        Action: CopySignerAction
        Purpose: CopySignerPurpose
        PublicKeySha256: byte array
        Role: CopySignerApprovalRole
        ExpiresAt: DateTimeOffset
    }

[<RequireQualifiedAccess>]
type CopySignerApprovalOutcome =
    | Approved of approvalId: Guid * authorityRevision: int64
    | ResourceUnavailable
    | StartedUnconfirmed of approvalId: Guid
