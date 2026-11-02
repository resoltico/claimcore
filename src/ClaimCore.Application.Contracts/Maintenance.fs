namespace ClaimCore.Application.Contracts

open System
open ClaimCore.Domain

/// Operational register state; it is not a business fact or a ledger projection.
type RegisterMaintenance =
    private
    | RegisterMaintenance of active: bool * generation: int64

    member this.Active = let (RegisterMaintenance(active, _)) = this in active
    member this.Generation = let (RegisterMaintenance(_, generation)) = this in generation

module RegisterMaintenance =
    let initial = RegisterMaintenance(false, 0L)

    let create active generation =
        if
            generation < 0L
            || generation > SafeInteger.Maximum
            || (active && generation = 0L)
        then
            Error "Invalid maintenance generation."
        else
            Ok(RegisterMaintenance(active, generation))

    let enter (current: RegisterMaintenance) =
        if current.Active then
            Ok current
        elif current.Generation = SafeInteger.Maximum then
            Error current
        else
            Ok(RegisterMaintenance(true, current.Generation + 1L))

    let resume expected (current: RegisterMaintenance) =
        if current.Active && current.Generation = expected then
            Ok(RegisterMaintenance(false, current.Generation))
        else
            Error current

[<RequireQualifiedAccess>]
type MaintenanceChange =
    | Enter
    | Resume of expectedGeneration: int64

type MaintenanceObservation =
    {
        State: RegisterMaintenance
        ObservedAt: DateTimeOffset
        Position: LedgerPosition
        Changed: bool
    }
