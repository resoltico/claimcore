namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open DataAuditCommon

/// Replays the complete writer-generation chain and both witnessed handoff tickets.
module internal DataAuditWriterHandoffs =
    let private projection (connection: NpgsqlConnection) transaction =
        use command =
            new NpgsqlCommand(
                "SELECT writer_generation,writer_handoff_event_id,"
                + "writer_handoff_sequence,writer_handoff_hash "
                + "FROM claimcore.installation_lineage WHERE singleton",
                connection,
                transaction
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            corrupt ()

        let generation = reader.GetInt64(0)

        let optional index read =
            if reader.IsDBNull(index) then None else Some(read index)

        let result =
            generation,
            optional 1 reader.GetGuid,
            optional 2 reader.GetInt64,
            optional 3 reader.GetFieldValue<byte array>

        if reader.Read() then
            corrupt ()

        result

    let verify connection transaction (witness: WitnessProtocol) cutoff (ct: CancellationToken) =
        task {
            let mutable generation = 1L
            let mutable more = true
            let mutable count = 0L
            let mutable last: (Guid * int64 * byte array) option = None

            while more do
                let! rows = DataAuditWriterHandoffRows.page connection transaction generation ct

                for row in rows do
                    if row.OldGeneration <> generation || row.NewGeneration <> generation + 1L then
                        corrupt ()

                    DataAuditWriterHandoffEvidence.verify witness cutoff row |> ignore

                    generation <- row.NewGeneration
                    last <- Some(row.HandoffId, row.SettlementSequence, row.SettlementHash)
                    count <- count + 1L

                more <- rows.Length = 50

            let storedGeneration, storedId, storedSequence, storedHash =
                projection connection transaction

            let tip = witness.Snapshot()

            if
                storedGeneration <> generation
                || tip.WriterGeneration <> generation
                || (match last with
                    | None -> storedId.IsSome || storedSequence.IsSome || storedHash.IsSome
                    | Some(id, sequence, hash) ->
                        storedId <> Some id
                        || storedSequence <> Some sequence
                        || storedHash <> Some hash)
            then
                corrupt ()

            return count
        }
