module internal ClaimCore.Application.OwnerTerminalDecisionData

open System
open ClaimCore.Domain

/// The storage layer obtains this data while holding the tombstone authority transaction.
type Projection =
    {
        SourceRevision: int64
        Disposition: CaseDisposition
        Privacy: PrivacyPhase
        ReferenceCommitment: byte array
    }

/// The storage layer verifies each current HUMAN DATA_STEWARD grant and exact witnessed row.
type ApprovalEvidence =
    {
        ApprovalId: Guid
        ActorId: Guid
        GrantRevision: int64
        WitnessSequence: int64
        WitnessHash: string
        ExpiresAt: DateTimeOffset
    }
