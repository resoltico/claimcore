namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open DataAuditCommon

/// A W1 settlement may be the sole latest pending generation; all earlier generations
/// require exact W2 activation evidence and a matching current pair projection.
module internal DataAuditWriterActivations =
    let private projection (connection: NpgsqlConnection) transaction : PrimaryActivationState =
        use command =
            new NpgsqlCommand(
                "SELECT writer_generation,writer_activation_pending,writer_activation_event_id,"
                + "writer_activation_sequence,writer_activation_hash "
                + "FROM claimcore.installation_lineage WHERE singleton",
                connection,
                transaction
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            corrupt ()

        let optional index read =
            if reader.IsDBNull(index) then None else Some(read index)

        let state =
            {
                Generation = reader.GetInt64(0)
                Pending = reader.GetBoolean(1)
                ActivationId = optional 2 reader.GetGuid
                ActivationSequence = optional 3 reader.GetInt64
                ActivationHash = optional 4 reader.GetFieldValue<byte array>
            }

        if reader.Read() then
            corrupt ()

        state

    let private count (connection: NpgsqlConnection) transaction =
        use command =
            new NpgsqlCommand(
                "SELECT count(*) FROM claimcore.writer_activations",
                connection,
                transaction
            )

        command.ExecuteScalar() :?> int64

    let private pairMatches (state: PrimaryActivationState) (snapshot: ClaimCore.Witness.Snapshot) =
        state.Generation = snapshot.WriterGeneration
        && state.Pending = snapshot.ActivationPending
        && state.ActivationId = snapshot.ActivationEventId
        && state.ActivationSequence = snapshot.ActivationSequence
        && state.ActivationHash = snapshot.ActivationHash

    let private pendingMatches (state: PrimaryActivationState) expected =
        state.Generation > 1L
        && expected = state.Generation
        && state.ActivationId.IsNone
        && state.ActivationSequence.IsNone
        && state.ActivationHash.IsNone

    let private activeMatches (state: PrimaryActivationState) expected last =
        match last with
        | Some(id, sequence, hash) ->
            expected = state.Generation + 1L
            && state.ActivationId = Some id
            && state.ActivationSequence = Some sequence
            && state.ActivationHash = Some hash
        | None -> false

    let private finalState
        (state: PrimaryActivationState)
        (snapshot: ClaimCore.Witness.Snapshot)
        expected
        (last: (Guid * int64 * byte array) option)
        =
        if not (pairMatches state snapshot) then
            corrupt ()

        if state.Pending then
            if not (pendingMatches state expected) then
                corrupt ()
        elif state.Generation = 1L then
            if expected <> 2L || last.IsSome || state.ActivationId.IsSome then
                corrupt ()
        elif not (activeMatches state expected last) then
            corrupt ()

    let verify connection transaction (witness: WitnessProtocol) cutoff (ct: CancellationToken) =
        task {
            let mutable expected = 2L
            let mutable scanned = 0L
            let mutable last: (Guid * int64 * byte array) option = None
            let mutable reading = true

            while reading do
                let! row =
                    DataAuditWriterActivationRows.next
                        connection
                        transaction
                        (expected - 1L)
                        witness.Identity
                        ct

                match row with
                | None -> reading <- false
                | Some value ->
                    if value.Evidence.WriterGeneration <> expected then
                        corrupt ()

                    do! DataAuditWriterActivationEvidence.verify witness cutoff value ct
                    last <- Some(value.ActivationId, value.SettlementSequence, value.SettlementHash)
                    expected <- expected + 1L
                    scanned <- scanned + 1L

            if scanned <> count connection transaction then
                corrupt ()

            let! snapshot = witness.Snapshot(ct)
            finalState (projection connection transaction) snapshot expected last
            return scanned
        }
