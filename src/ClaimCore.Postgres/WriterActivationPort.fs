namespace ClaimCore.Postgres

open System
open System.Threading
open System.Threading.Tasks

/// Exact signed post-handoff evidence supplied by owner-private restore verification.
/// The separately verified host proof is returned through IWriterActivationEvidenceVerifier.
[<NoEquality; NoComparison>]
type internal WriterActivationEvidence =
    {
        HandoffId: Guid
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        WriterGeneration: int64
        W1Sequence: int64
        W1Hash: byte array
        PublicationManifestSha256: byte array
        ReportSha256: byte array
        FenceSha256: byte array
        SupplementSha256: byte array
        FinalWalObjectSha256: byte array
        FinalWalObjectCount: int
        IndependentProbeSha256: byte array
        ProbeEvidenceSha256: byte array
        CheckpointSigningKeyId: Guid
        CheckpointHolderActorId: Guid
        SignedReport: byte array
        ReportSignature: byte array
        SignedFence: byte array
        FenceSignature: byte array
        SignedSupplement: byte array
        SupplementSignature: byte array
        ValidUntil: DateTimeOffset
    }

/// A verifier-owned qualification of independent-host and publication evidence. A digest or
/// caller Boolean by itself cannot produce this value in the production Database composition.
[<NoEquality; NoComparison>]
type internal WriterActivationQualification =
    {
        HandoffId: Guid
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        WriterGeneration: int64
        W1Sequence: int64
        W1Hash: byte array
        PublicationManifestSha256: byte array
        ReportSha256: byte array
        FenceSha256: byte array
        SupplementSha256: byte array
        FinalWalObjectSha256: byte array
        FinalWalObjectCount: int
        IndependentProbeSha256: byte array
        ProbeEvidenceSha256: byte array
        CheckpointSigningKeyId: Guid
        CheckpointHolderActorId: Guid
        ValidUntil: DateTimeOffset
    }

type internal IWriterActivationEvidenceVerifier =
    abstract Verify:
        evidence: WriterActivationEvidence * cancellationToken: CancellationToken ->
            Task<WriterActivationQualification option>

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type internal WriterActivationOutcome =
    | Activated of activationId: Guid * witnessSequence: int64 * witnessHash: byte array
    | Refused
    | Unconfirmed of activationId: Guid
