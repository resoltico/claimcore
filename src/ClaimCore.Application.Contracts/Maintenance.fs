namespace ClaimCore.Application.Contracts

open System
open ClaimCore.Domain

/// Operational register state; it is not a business fact or a ledger projection.
type RegisterMaintenance =
    private
    | RegisterMaintenance of active: bool

    member this.Active = let (RegisterMaintenance active) = this in active

module RegisterMaintenance =
    let initial = RegisterMaintenance false
    let create active = RegisterMaintenance active

[<RequireQualifiedAccess>]
type MaintenanceChange =
    | On
    | Off

type MaintenanceObservation =
    {
        State: RegisterMaintenance
        ObservedAt: DateTimeOffset
        Position: LedgerPosition
        Changed: bool
    }
