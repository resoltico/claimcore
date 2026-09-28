namespace ClaimCore.Hosting

open System
open Npgsql
open ClaimCore.Postgres
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type private PrimaryWriterState =
    {
        Generation: int64
        HandoffId: Guid option
        HandoffSequence: int64 option
        HandoffHash: byte array option
        ActivationPending: bool
        ActivationId: Guid option
        ActivationSequence: int64 option
        ActivationHash: byte array option
        LastAbortId: Guid option
        LastAbortSequence: int64 option
        LastAbortHash: byte array option
        LossRetired: bool
    }

/// Checks the current primary/witness cutover fence for each actor operation. Read leases
/// hold the witness row lock until the core finishes producing its claimant-bearing outcome.
type internal RuntimeSafetySupervisor(resources: RuntimeResources) =
    let witness = resources.Witness

    let primaryState () =
        use connection = resources.DataSource.OpenConnection()

        use command =
            new NpgsqlCommand(
                "SELECT installation_id,lineage_id,witness_epoch,writer_generation,"
                + "writer_handoff_event_id,writer_handoff_sequence,writer_handoff_hash,"
                + "writer_activation_pending,writer_activation_event_id,"
                + "writer_activation_sequence,writer_activation_hash,"
                + "last_aborted_handoff_id,last_aborted_handoff_sequence,last_aborted_handoff_hash,"
                + "loss_retired "
                + "FROM claimcore.installation_lineage WHERE singleton",
                connection
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Writer authority is unavailable."

        let identity = witness.Identity

        if
            reader.GetGuid(0) <> identity.InstallationId
            || reader.GetGuid(1) <> identity.LineageId
            || reader.GetInt64(2) <> identity.Epoch
        then
            invalidOp "Writer identity differs."

        let optional index read =
            if reader.IsDBNull(index) then None else Some(read index)

        let state =
            {
                Generation = reader.GetInt64(3)
                HandoffId = optional 4 reader.GetGuid
                HandoffSequence = optional 5 reader.GetInt64
                HandoffHash = optional 6 reader.GetFieldValue<byte array>
                ActivationPending = reader.GetBoolean(7)
                ActivationId = optional 8 reader.GetGuid
                ActivationSequence = optional 9 reader.GetInt64
                ActivationHash = optional 10 reader.GetFieldValue<byte array>
                LastAbortId = optional 11 reader.GetGuid
                LastAbortSequence = optional 12 reader.GetInt64
                LastAbortHash = optional 13 reader.GetFieldValue<byte array>
                LossRetired = reader.GetBoolean(14)
            }

        if reader.Read() then
            invalidOp "Writer authority is ambiguous."

        state

    let opening = primaryState ()

    let sameGeneration (current: PrimaryWriterState) (snapshot: ClaimCore.Witness.Snapshot) =
        current.Generation = opening.Generation
        && current.Generation = snapshot.WriterGeneration
        && not snapshot.HandoffPending
        && not current.ActivationPending
        && not snapshot.ActivationPending

    let sameActivation (current: PrimaryWriterState) (snapshot: ClaimCore.Witness.Snapshot) =
        current.ActivationId = opening.ActivationId
        && current.ActivationSequence = opening.ActivationSequence
        && current.ActivationHash = opening.ActivationHash
        && current.ActivationId = snapshot.ActivationEventId
        && current.ActivationSequence = snapshot.ActivationSequence
        && current.ActivationHash = snapshot.ActivationHash

    let sameAbort (current: PrimaryWriterState) (snapshot: ClaimCore.Witness.Snapshot) =
        current.LastAbortId = opening.LastAbortId
        && current.LastAbortSequence = opening.LastAbortSequence
        && current.LastAbortHash = opening.LastAbortHash
        && current.LastAbortId = snapshot.LastAbortedHandoffId
        && current.LastAbortSequence = snapshot.LastAbortedHandoffSequence
        && current.LastAbortHash = snapshot.LastAbortedHandoffHash

    let requirePair () =
        let current = primaryState ()
        let snapshot = witness.Snapshot()

        if current.LossRetired || snapshot.LossRetirementPending || snapshot.LossRetired then
            invalidOp "Writer lineage is terminally quarantined."

        if
            not (sameGeneration current snapshot)
            || not (sameActivation current snapshot)
            || not (sameAbort current snapshot)
        then
            invalidOp "Writer generation is quarantined."

        match current.HandoffId, current.HandoffSequence, current.HandoffHash with
        | None, None, None when current.Generation = 1L -> ()
        | Some eventId, Some sequence, Some hash when current.Generation > 1L ->
            WitnessProtocolHandoff.verifySettlement witness eventId sequence hash

            match current.ActivationId, current.ActivationSequence, current.ActivationHash with
            | Some activationId, Some activationSequence, Some activationHash ->
                use connection = resources.DataSource.OpenConnection()

                WriterActivationRead.verify
                    connection
                    witness
                    eventId
                    current.Generation
                    sequence
                    hash
                    activationId
                    activationSequence
                    activationHash
            | _ -> invalidOp "Restored writer activation ticket is incomplete."
        | _ -> invalidOp "Writer handoff ticket is incomplete."

    let useState () =
        use connection = resources.DataSource.OpenConnection()
        InstallationUseScopeRead.requirePair connection witness

    let requireCaseRead () =
        requirePair ()
        let state = useState ()

        if
            state.Scope = InstallationUseScope.RealData
            && state.Phase <> InstallationUsePhase.Active
        then
            invalidOp "Real-data case access is not activated."

    let requireCaseMutation () =
        requirePair ()
        let state = useState ()

        if
            state.Scope = InstallationUseScope.RealData
            && state.Phase <> InstallationUsePhase.Active
        then
            invalidOp "Real-data case mutation is not activated."

        RuntimeBackupHealthFiles.require resources state

    let requireAuthoritySetup () =
        // The terminal loss fence closes even the typed actor setup lane.
        requirePair ()
        useState () |> ignore

    do
        requirePair ()
        let state = useState ()

        if state.Phase = InstallationUsePhase.Active then
            RuntimeBackupHealthFiles.require resources state

    member _.RequireCurrent() =
        requirePair ()
        useState () |> ignore

    member _.CurrentUseState() = useState ()

    member _.RequireCaseRead() = requireCaseRead ()
    member _.RequireCaseMutation() = requireCaseMutation ()
    member _.RequireAuthoritySetup() = requireAuthoritySetup ()

    member _.RequireAuthorityRead() =
        requirePair ()
        useState () |> ignore

    member _.AcquireReadFence() =
        let lease = witness.AcquireReadFence(opening.Generation)

        try
            requirePair ()
            lease
        with _ ->
            lease.Dispose()
            reraise ()
