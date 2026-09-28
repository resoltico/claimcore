namespace ClaimCore.Postgres

open System
open System.Threading
open System.Threading.Tasks
open Npgsql

/// Created only after the owner process verifies private signed evidence under the copy lock.
[<NoEquality; NoComparison>]
type internal VerifiedManagedCopyDeletionAbsence =
    {
        CopyId: Guid
        RegistrySha256: byte array
        InspectionReportSha256: byte array
        RegistryHolderActorId: Guid
        VerifierSigningKeyId: Guid
        VerifierHolderActorId: Guid
        WitnessCutoffSequence: int64
        WitnessCutoffHash: byte array
        ObservedAt: DateTimeOffset
        RegistryExpiresAt: DateTimeOffset
        InspectionExpiresAt: DateTimeOffset
    }

type internal ICopyAbsenceVerifier =
    abstract Verify:
        primary: NpgsqlConnection *
        transaction: NpgsqlTransaction *
        witness: WitnessProtocol *
        copyId: Guid *
        cutoffSequence: int64 *
        cutoffHash: byte array *
        checkedAt: DateTimeOffset *
        cancellationToken: CancellationToken ->
            Task<VerifiedManagedCopyDeletionAbsence option>
