namespace ClaimCore.Postgres

open System
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type internal OwnerWitnessPruneOutcome =
    | WitnessPayloadPruned of eventId: Guid * deletedCiphertextRows: int64
    | Refused of LifecycleRefusal
    | ResourceUnavailable
    | InventoryUnknown
    | AuditUnavailable of safeStage: string
    | Unconfirmed of eventId: Guid
