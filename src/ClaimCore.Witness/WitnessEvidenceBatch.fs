namespace ClaimCore.Witness

open System
open System.Collections.Generic
open System.Threading
open Npgsql
open NpgsqlTypes

/// Eight exact proof units, each with an intent and its required settlement. At most 16 MiB
/// of ciphertext (8 * 2 * the existing 1 MiB envelope maximum), not a journal scan.
module internal WitnessEvidenceBatch =
    let private commandFor (identity: Identity) (connection: NpgsqlConnection) subjects =
        let command =
            new NpgsqlCommand(
                "SELECT j.sequence,CASE WHEN octet_length(j.entry_hash)=32 THEN j.entry_hash END,CASE WHEN octet_length(j.payload_sha256)=32 THEN j.payload_sha256 END,j.epoch,j.lineage_id,"
                + "CASE WHEN octet_length(p.encrypted_payload) BETWEEN 1 AND 1048576 THEN p.encrypted_payload END,"
                + "j.key_id,j.scope_kind,j.subject_case_id,j.operation_id,j.phase,octet_length(p.encrypted_payload) "
                + "FROM unnest(@operations::uuid[],@settlements::text[]) s(operation,settlement) "
                + "JOIN claimcore_witness.journal j ON j.operation_id=s.operation AND j.phase IN ('INTENT',s.settlement) "
                + "LEFT JOIN claimcore_witness.journal_payloads p ON p.installation_id=j.installation_id "
                + "AND p.sequence=j.sequence AND p.subject_case_id IS NOT DISTINCT FROM j.subject_case_id "
                + "WHERE j.installation_id=@installation ORDER BY j.operation_id,j.sequence",
                connection
            )

        command.Parameters.AddWithValue("installation", NpgsqlDbType.Uuid, identity.InstallationId)
        |> ignore

        command.Parameters.AddWithValue(
            "operations",
            NpgsqlDbType.Array ||| NpgsqlDbType.Uuid,
            subjects |> List.map fst |> List.toArray
        )
        |> ignore

        command.Parameters.AddWithValue(
            "settlements",
            NpgsqlDbType.Array ||| NpgsqlDbType.Text,
            subjects |> List.map (snd >> Encoding.phase) |> List.toArray
        )
        |> ignore

        command

    let read
        (identity: Identity)
        (connection: NpgsqlConnection)
        (subjects: (Guid * Phase) list)
        (ct: CancellationToken)
        =
        task {
            if
                subjects.Length < 1
                || subjects.Length > 8
                || Set.count (Set.ofList subjects) <> subjects.Length
            then
                invalidArg (nameof subjects) "Witness proof subjects must be distinct and bounded."

            use command = commandFor identity connection subjects
            use! reader = command.ExecuteReaderAsync(ct)
            let rows = Dictionary<Guid * Phase, Evidence>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(ct)

                if not found then
                    reading <- false
                else
                    // SQL omits oversized bytes before the driver can buffer/materialize them.
                    let operation, phase =
                        reader.GetGuid(9), Encoding.parsePhase (reader.GetString(10))

                    if
                        reader.IsDBNull(11)
                        || reader.GetInt32(11) < 1
                        || reader.GetInt32(11) > 1048576
                    then
                        invalidOp "Witness proof payload is unavailable."

                    let evidence = WitnessStoreRead.readCommitted identity operation phase reader

                    if
                        rows.Count >= 2 * subjects.Length
                        || not (rows.TryAdd((operation, phase), evidence))
                    then
                        invalidOp "Witness proof is ambiguous."

            return rows
        }
