namespace ClaimCore.Postgres

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type internal SignedCopyAdoptionDocument =
    {
        Canonical: byte array
        Signature: byte array
    }

[<NoEquality; NoComparison>]
type internal CopyAdoptionSubmission =
    {
        ApprovalId: Guid
        AdoptionEventId: Guid
        Custodian: SignedCopyAdoptionDocument
        Registry: SignedCopyAdoptionDocument
        Inspection: SignedCopyAdoptionDocument
    }

/// A positive result can only be issued after the Database owner adapter opens the private
/// mapped file, recomputes keyed commitments and reads/hash-checks the actual ciphertext.
/// A digest or claimed path alone cannot construct a valid owner adoption.
[<Sealed>]
type internal VerifiedCopyAdoptionPrivateLocation
    private
    (
        copyId: Guid,
        caseId: Guid,
        locationCommitment: byte array,
        custodianCommitment: byte array,
        ciphertextSha256: byte array,
        ciphertextBytes: int64,
        observedAt: DateTimeOffset,
        expiresAt: DateTimeOffset
    ) =
    member _.CopyId = copyId
    member _.CaseId = caseId
    member _.LocationCommitment = Array.copy locationCommitment
    member _.CustodianCommitment = Array.copy custodianCommitment
    member _.CiphertextSha256 = Array.copy ciphertextSha256
    member _.CiphertextBytes = ciphertextBytes
    member _.ObservedAt = observedAt
    member _.ExpiresAt = expiresAt

    static member internal FromVerifiedOpenFile
        (
            copyId,
            caseId,
            locationCommitment: byte array,
            custodianCommitment: byte array,
            ciphertextSha256: byte array,
            ciphertextBytes,
            observedAt: DateTimeOffset,
            expiresAt: DateTimeOffset
        ) =
        if
            copyId = Guid.Empty
            || caseId = Guid.Empty
            || locationCommitment.Length <> 32
            || custodianCommitment.Length <> 32
            || ciphertextSha256.Length <> 32
            || ciphertextBytes < 1L
            || observedAt.Offset <> TimeSpan.Zero
            || expiresAt.Offset <> TimeSpan.Zero
            || expiresAt <= observedAt
        then
            invalidArg (nameof copyId) "Private copy location evidence is invalid."

        VerifiedCopyAdoptionPrivateLocation(
            copyId,
            caseId,
            Array.copy locationCommitment,
            Array.copy custodianCommitment,
            Array.copy ciphertextSha256,
            ciphertextBytes,
            observedAt,
            expiresAt
        )

type internal ICopyAdoptionPrivateLocation =
    abstract Verify:
        primary: NpgsqlConnection *
        transaction: NpgsqlTransaction *
        witness: WitnessProtocol *
        request: CopyAdoptionApprovalRequest *
        submission: CopyAdoptionSubmission *
        observedAt: DateTimeOffset *
        cancellationToken: CancellationToken ->
            Task<VerifiedCopyAdoptionPrivateLocation option>

[<RequireQualifiedAccess>]
type internal CopyAdoptionOwnerOutcome =
    | Adopted of eventId: Guid * revision: int64
    | ResourceUnavailable
    | PrivateLocationUnknown
    | AuditUnavailable of safeStage: string
    | Unconfirmed of eventId: Guid
