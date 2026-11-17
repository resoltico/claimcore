namespace ClaimCore.Application.Contracts

open System
open ClaimCore.Domain

/// A coordination handle is not a credential. Generations never decrease or expire.
type MaintenanceHandle =
    private
        {
            GenerationValue: int64
            PauseIdValue: Guid
        }

    member this.Generation = this.GenerationValue
    member this.PauseId = this.PauseIdValue

module MaintenanceHandle =
    let create generation pauseId =
        if generation > 0L && generation <= SafeInteger.Maximum && pauseId <> Guid.Empty then
            Some
                {
                    GenerationValue = generation
                    PauseIdValue = pauseId
                }
        else
            None

/// Operational coordination survives process restarts, independently of the ledger.
type RegisterMaintenance =
    private
        {
            GenerationValue: int64
            PauseIdValue: Guid option
        }

    member this.Generation = this.GenerationValue
    member this.PauseId = this.PauseIdValue
    member this.Active = this.PauseId.IsSome

    member this.Handle =
        this.PauseId |> Option.bind (MaintenanceHandle.create this.Generation)

module RegisterMaintenance =
    let create generation (pauseId: Guid option) =
        if
            generation < 0L
            || generation > SafeInteger.Maximum
            || (pauseId.IsSome
                && (MaintenanceHandle.create generation pauseId.Value |> Option.isNone))
        then
            invalidArg "generation" "Invalid maintenance state."

        {
            GenerationValue = generation
            PauseIdValue = pauseId
        }

    let initial = create 0L None

[<RequireQualifiedAccess>]
type MaintenanceChange =
    | Acquire of expectedGeneration: int64 * pauseId: Guid
    | Resume of MaintenanceHandle
    | Takeover of MaintenanceHandle * pauseId: Guid

module MaintenanceChange =
    /// Same active acquisition is retryable; a released acquisition can never recreate a pause.
    let apply (current: RegisterMaintenance) change =
        let advance pause =
            if current.Generation >= SafeInteger.Maximum then
                None
            else
                Some(RegisterMaintenance.create (current.Generation + 1L) pause)

        match change with
        | MaintenanceChange.Acquire(expected, id) when
            expected >= 0L && expected < SafeInteger.Maximum && id <> Guid.Empty
            ->
            if current.PauseId = Some id && current.Generation = expected + 1L then
                Some current
            elif current.Generation = expected && not current.Active then
                advance (Some id)
            else
                None
        | MaintenanceChange.Resume handle when current.Handle = Some handle -> advance None
        | MaintenanceChange.Takeover(handle, id) when
            current.Handle = Some handle && id <> Guid.Empty && current.PauseId <> Some id
            ->
            advance (Some id)
        | _ -> None

type MaintenanceObservation =
    {
        State: RegisterMaintenance
        ObservedAt: DateTimeOffset
        Position: LedgerPosition
        Changed: bool
    }
