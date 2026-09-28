namespace ClaimCore.Database

open System
open System.IO
open Npgsql
open ClaimCore.HostSecurity
open ClaimCore.Postgres

module internal DatabaseRestoreFencedTailLive =
    let primaryW1
        (owner: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (tail: FencedTailClaims)
        =
        use command =
            new NpgsqlCommand(
                "SELECT h.settlement_sequence,h.settlement_hash,l.writer_generation "
                + "FROM claimcore.writer_handoffs h CROSS JOIN claimcore.installation_lineage l "
                + "WHERE h.handoff_id=@handoff AND l.singleton",
                owner,
                transaction
            )

        Sql.uuid command "handoff" tail.HandoffId
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Fenced recovery tail has no primary W1 settlement."

        let sequence = reader.GetInt64(0)
        let hash = Convert.ToHexStringLower(reader.GetFieldValue<byte array>(1))
        let generation = reader.GetInt64(2)

        if
            reader.Read()
            || sequence <> tail.W1Sequence
            || hash <> tail.W1Hash
            || generation <> tail.NewGeneration
        then
            invalidOp "Fenced recovery tail differs from primary W1 settlement."

    let archiveObjects configured (tail: FencedTailClaims) =
        if configured <> tail.ArchiveRoot then
            invalidOp "Fenced WAL archive differs from owner-private configuration."

        match PrivateFileService.requirePrivateDirectory configured with
        | Ok() -> ()
        | Error _ -> invalidOp "Fenced WAL archive root is unsafe."

        for item in tail.WalObjects do
            let path = Path.Combine(configured, item.RelativePath)

            match PrivateFileService.hashPrivateFile item.CiphertextBytes path with
            | Ok(length, hash) when
                length = item.CiphertextBytes
                && Convert.ToHexStringLower(hash) = item.CiphertextSha256
                ->
                ()
            | _ -> invalidOp "Fenced encrypted WAL object is missing or changed."
