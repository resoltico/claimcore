namespace ClaimCore.Hosting

open System
open System.Data
open System.Threading
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

type private OrderedReadFence
    (primary: NpgsqlConnection, transaction: NpgsqlTransaction, witnessLease: IDisposable) =
    let mutable disposed = 0

    interface IDisposable with
        member _.Dispose() =
            if Interlocked.Exchange(&disposed, 1) = 0 then
                try
                    witnessLease.Dispose()
                finally
                    try
                        transaction.Dispose()
                    finally
                        primary.Dispose()

/// Checks the current primary/witness cutover fence for each actor operation. Disclosure
/// leases take the primary shared authority lock before the witness read fence and hold both
/// until the core finishes producing its outcome.
type internal RuntimeSafetySupervisor(resources: RuntimeResources) =
    let witness = resources.Witness

    let primaryState (ct: CancellationToken) =
        task {
            use! connection = resources.DataSource.OpenConnectionAsync(ct)

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

            use! reader = command.ExecuteReaderAsync(ct)

            let! found = reader.ReadAsync(ct)

            if not found then
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

            let! duplicated = reader.ReadAsync(ct)

            if duplicated then
                invalidOp "Writer authority is ambiguous."

            return state
        }

    let mutable openingState: PrimaryWriterState option = None

    let opening () =
        openingState
        |> Option.defaultWith (fun () -> invalidOp "Runtime safety is not initialized.")

    let sameGeneration (current: PrimaryWriterState) (snapshot: ClaimCore.Witness.Snapshot) =
        current.Generation = (opening ()).Generation
        && current.Generation = snapshot.WriterGeneration
        && not snapshot.HandoffPending
        && not current.ActivationPending
        && not snapshot.ActivationPending

    let sameActivation (current: PrimaryWriterState) (snapshot: ClaimCore.Witness.Snapshot) =
        current.ActivationId = (opening ()).ActivationId
        && current.ActivationSequence = (opening ()).ActivationSequence
        && current.ActivationHash = (opening ()).ActivationHash
        && current.ActivationId = snapshot.ActivationEventId
        && current.ActivationSequence = snapshot.ActivationSequence
        && current.ActivationHash = snapshot.ActivationHash

    let sameAbort (current: PrimaryWriterState) (snapshot: ClaimCore.Witness.Snapshot) =
        current.LastAbortId = (opening ()).LastAbortId
        && current.LastAbortSequence = (opening ()).LastAbortSequence
        && current.LastAbortHash = (opening ()).LastAbortHash
        && current.LastAbortId = snapshot.LastAbortedHandoffId
        && current.LastAbortSequence = snapshot.LastAbortedHandoffSequence
        && current.LastAbortHash = snapshot.LastAbortedHandoffHash

    let requirePair ct =
        task {
            let! current = primaryState ct
            let! snapshot = witness.Snapshot(ct)

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
                do! WitnessProtocolHandoff.verifySettlement witness eventId sequence hash ct

                match current.ActivationId, current.ActivationSequence, current.ActivationHash with
                | Some activationId, Some activationSequence, Some activationHash ->
                    use! connection = resources.DataSource.OpenConnectionAsync(ct)

                    do!
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
                            ct
                | _ -> invalidOp "Restored writer activation ticket is incomplete."
            | _ -> invalidOp "Writer handoff ticket is incomplete."
        }

    let useState ct =
        task {
            use! connection = resources.DataSource.OpenConnectionAsync(ct)
            return! InstallationUseScopeRead.requirePair connection witness ct
        }

    let requireCaseRead ct =
        task {
            do! requirePair ct
            let! state = useState ct

            if
                state.Scope = InstallationUseScope.RealData
                && state.Phase <> InstallationUsePhase.Active
            then
                invalidOp "Real-data case access is not activated."
        }

    let requireCaseMutation ct =
        task {
            do! requirePair ct
            let! state = useState ct

            if
                state.Scope = InstallationUseScope.RealData
                && state.Phase <> InstallationUsePhase.Active
            then
                invalidOp "Real-data case mutation is not activated."

            do! RuntimeBackupHealthFiles.require resources state ct
        }

    let requireAuthoritySetup ct =
        task {
            // The terminal loss fence closes even the typed actor setup lane.
            do! requirePair ct
            let! _ = useState ct
            return ()
        }

    member _.Initialize(ct) =
        task {
            let! initial = primaryState ct

            if openingState.IsSome then
                invalidOp "Runtime safety was already initialized."

            openingState <- Some initial
            do! requirePair ct
            let! state = useState ct

            if state.Phase = InstallationUsePhase.Active then
                do! RuntimeBackupHealthFiles.require resources state ct

            return state
        }

    member _.RequireCurrent(ct) = requireAuthoritySetup ct
    member _.CurrentUseState(ct) = useState ct
    member _.RequireCaseRead(ct) = requireCaseRead ct
    member _.RequireCaseMutation(ct) = requireCaseMutation ct
    member _.RequireAuthoritySetup(ct) = requireAuthoritySetup ct
    member _.RequireAuthorityRead(ct) = requireAuthoritySetup ct

    member _.AcquireReadFence(ct) =
        task {
            // Writers take primary authority before exclusive witness authority. A read
            // must use that order too; its separate pool cannot strand nested core reads.
            let! primary = resources.ReadBarrierDataSource.OpenConnectionAsync(ct)

            try
                let! transaction = primary.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)

                try
                    use command =
                        new NpgsqlCommand(
                            "SELECT revision FROM claimcore.authority_tip WHERE singleton FOR SHARE",
                            primary,
                            transaction
                        )

                    let! revision = command.ExecuteScalarAsync(ct)

                    if not (revision :? int64) then
                        invalidOp "Primary read barrier is unavailable."

                    let! lease = witness.AcquireReadFence((opening ()).Generation, ct)

                    try
                        do! requirePair ct
                        return new OrderedReadFence(primary, transaction, lease) :> IDisposable
                    with error ->
                        lease.Dispose()
                        return raise error
                with error ->
                    transaction.Dispose()
                    return raise error
            with error ->
                primary.Dispose()
                return raise error
        }
