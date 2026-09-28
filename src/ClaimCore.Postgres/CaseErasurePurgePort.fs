namespace ClaimCore.Postgres

open System
open System.Threading
open System.Threading.Tasks
open Npgsql

[<RequireQualifiedAccess>]
type internal OwnerPurgeRefusal =
    | HoldActive
    | ApprovalsIncomplete
    | ProposalMismatch
    | ErasureNotPending

[<RequireQualifiedAccess>]
type internal OwnerPurgeOutcome =
    | Purged of eventId: Guid * deletedLiveRows: int64
    | Refused of OwnerPurgeRefusal
    | InventoryUnknown
    | IdentityCoverageUnknowable
    | AuditUnavailable of safeStage: string
    | Unconfirmed of eventId: Guid

/// This is owner-trusted evidence from a complete, witnessed copy inventory inspection. It is
/// not a caller flag or a claim that retained physical copies have already been deleted.
[<NoEquality; NoComparison>]
type internal ManagedCopyInventorySeal =
    {
        CaseId: Guid
        WitnessCutoffSequence: int64
        WitnessCutoffHash: byte array
        InventorySha256: byte array
        CopyCount: int64
        ObservedAt: DateTimeOffset
    }

/// The owner process must supply a concrete auditor that derives this seal from registered,
/// signed location evidence and fails closed on unknown/unregistered copies.
type internal IManagedCopyErasureClearance =
    abstract RequireCompleteInventory:
        primary: NpgsqlConnection *
        transaction: NpgsqlTransaction *
        witness: WitnessProtocol *
        caseId: Guid *
        cutoffSequence: int64 *
        cutoffHash: byte array *
        cancellationToken: CancellationToken ->
            Task<ManagedCopyInventorySeal option>
