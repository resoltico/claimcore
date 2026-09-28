namespace ClaimCore.Witness

open System

[<NoEquality; NoComparison>]
type internal WriterHandoffEvidence =
    {
        HandoffId: Guid
        OldGeneration: int64
        NewGeneration: int64
        PreviousSequence: int64
        PreviousHash: byte array
        NewCapabilitySha256: byte array
        CheckpointSigningKeyId: Guid
        PrepareCanonical: byte array
        PrepareSignature: byte array
        ApprovalOneId: Guid
        ApprovalTwoId: Guid
        PrepareCandidateSha256: byte array
        PrepareSequence: int64
        PrepareHash: byte array
        PrepareRecordedAt: DateTimeOffset
        SettlementCandidateSha256: byte array option
        SettlementSequence: int64 option
        SettlementHash: byte array option
        SettlementCanonical: byte array option
        SettlementSignature: byte array option
        AbortCandidateSha256: byte array option
        AbortSequence: int64 option
        AbortHash: byte array option
        AbortCanonical: byte array option
        AbortSignatureOne: byte array option
        AbortSignatureTwo: byte array option
        AbortSigningKeyOne: Guid option
        AbortSigningKeyTwo: Guid option
        AbortRecordedAt: DateTimeOffset option
    }
