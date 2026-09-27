namespace ClaimCore.Postgres

open System
open System.Threading
open System.Threading.Tasks
open Npgsql

[<NoEquality; NoComparison>]
type internal ExternalCopyPublication =
    {
        PublicationId: Guid
        CopyId: Guid
        CaseId: Guid
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        EncryptionKeyId: Guid
        CiphertextSha256: byte array
        CiphertextBytes: int64
        CapturedAt: DateTimeOffset
        RetainUntil: DateTimeOffset
        LocationCommitment: byte array
        CustodianCommitment: byte array
        RegistrySigningKeyId: Guid
        IssuedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
    }

[<NoEquality; NoComparison>]
type internal ExternalCopyInspection =
    {
        PublicationId: Guid
        CopyId: Guid
        CaseId: Guid
        CiphertextSha256: byte array
        CiphertextBytes: int64
        LocationCommitment: byte array
        RegistryCanonicalSha256: byte array
        InspectorSigningKeyId: Guid
        ObservedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
    }

[<NoEquality; NoComparison>]
type internal ExternalCopyPublicationSubmission =
    {
        PublicationId: Guid
        Registry: SignedCopyAdoptionDocument
        Inspection: SignedCopyAdoptionDocument
    }

/// The owner-private adapter opens the actual mapped bytes and recomputes distinct keyed
/// commitments before issuing a positive proof. Publication proves pre-fence knowledge,
/// not that a future copy location is absent.
type internal IExternalCopyPublicationPrivateLocation =
    abstract Verify:
        primary: NpgsqlConnection *
        transaction: NpgsqlTransaction *
        publication: ExternalCopyPublication *
        submission: ExternalCopyPublicationSubmission *
        observedAt: DateTimeOffset *
        cancellationToken: CancellationToken ->
            Task<VerifiedCopyAdoptionPrivateLocation option>

[<RequireQualifiedAccess>]
type internal ExternalCopyPublicationOutcome =
    | Published of publicationId: Guid * witnessSequence: int64
    | ResourceUnavailable
    | PrivateLocationUnknown
    | AuditUnavailable of safeStage: string
    | Unconfirmed of publicationId: Guid
