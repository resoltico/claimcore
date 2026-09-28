namespace ClaimCore.Postgres

open System
open System.Threading
open System.Threading.Tasks

/// Only an owner-process verifier may produce this after recomputing signed publication,
/// restored-pair, copy inventory and independently pinned old-host fence evidence.
[<NoEquality; NoComparison>]
type internal WriterHandoffQualifiedEvidence =
    {
        PublicationManifestSha256: byte array
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        OldGeneration: int64
        ReviewedCutoffSequence: int64
        ReviewedCutoffHash: byte array
        NewCapabilitySha256: byte array
        RestoreReportSha256: byte array
        FenceReportSha256: byte array
        InventorySha256: byte array
        CheckpointSigningKeyId: Guid
        ValidUntil: DateTimeOffset
        IndependentProbeNonceSha256: byte array
    }

type internal IWriterHandoffEvidenceVerifier =
    abstract VerifyPreparation:
        proposal: WriterHandoffPreparation *
        canonical: byte array *
        cancellationToken: CancellationToken ->
            Task<WriterHandoffQualifiedEvidence option>

    abstract VerifySettlement:
        settlement: WriterHandoffSettlement *
        canonical: byte array *
        cancellationToken: CancellationToken ->
            Task<WriterHandoffQualifiedEvidence option>
