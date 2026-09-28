namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open Npgsql
open NpgsqlTypes
open ClaimCore.Application

module internal CaseTombstoneHoldPersistence =
    let private recordSql =
        "INSERT INTO claimcore.case_erasure_holds "
        + "(hold_id,case_id,ground_code,review_on,recorded_by,recorded_grant_revision,"
        + "recorded_at,record_event_id,record_revision,record_previous_hash,record_event_hash,"
        + "record_canonical_action,record_candidate_sha256,record_witness_sequence,"
        + "record_witness_epoch,record_witness_hash) VALUES "
        + "(@hold,@case,@code,@review,@actor,@grant,@instant,@event,@revision,"
        + "@previous,@hash,@canonical,@candidate,@sequence,@epoch,@witnessHash)"

    let private releaseSql =
        "INSERT INTO claimcore.case_erasure_hold_releases "
        + "(release_event_id,hold_id,case_id,release_code,released_by,"
        + "released_grant_revision,released_at,release_revision,release_previous_hash,"
        + "release_event_hash,release_canonical_action,release_candidate_sha256,"
        + "release_witness_sequence,release_witness_epoch,release_witness_hash) VALUES "
        + "(@event,@hold,@case,@code,@actor,@grant,@instant,@revision,@previous,"
        + "@hash,@canonical,@candidate,@sequence,@epoch,@witnessHash)"

    let private tipSql =
        "UPDATE claimcore.case_erasure_authority_tip SET revision=@next,event_hash=@hash "
        + "WHERE case_id=@case AND revision=@previousRevision AND event_hash=@previousHash"

    let private bind
        (command: NpgsqlCommand)
        (context: ActorCallContext)
        (change: TombstoneHoldChange)
        (stored: StoredCaseTombstone)
        (canonical: byte array)
        (intent: WitnessIntent)
        (instant: DateTimeOffset)
        =
        let ticket = intent.Ticket
        Sql.uuid command "event" change.EventId
        Sql.uuid command "case" change.CaseId
        Sql.uuid command "actor" context.Binding.ActorId
        Sql.integer command "grant" context.Binding.GrantRevision
        Sql.add command "instant" NpgsqlDbType.TimestampTz (box instant)
        Sql.integer command "revision" (stored.AuthorityRevision + 1L)
        Sql.add command "previous" NpgsqlDbType.Bytea (box stored.AuthorityHash)

        Sql.add
            command
            "hash"
            NpgsqlDbType.Bytea
            (box (CaseTombstoneCandidate.eventHash stored.AuthorityHash canonical))

        Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
        Sql.add command "candidate" NpgsqlDbType.Bytea (box intent.CandidateHash)
        Sql.integer command "sequence" ticket.Sequence
        Sql.integer command "epoch" ticket.Epoch
        Sql.add command "witnessHash" NpgsqlDbType.Bytea (box ticket.EntryHash)

        match change.Mutation with
        | TombstoneHoldMutation.Record(holdId, ground, reviewOn) ->
            Sql.uuid command "hold" holdId
            Sql.text command "code" ground
            Sql.add command "review" NpgsqlDbType.Date (box reviewOn)
        | TombstoneHoldMutation.Release(holdId, releaseCode) ->
            Sql.uuid command "hold" holdId
            Sql.text command "code" releaseCode

    let persist connection transaction context change stored canonical intent instant =
        task {
            let sql =
                match change.Mutation with
                | TombstoneHoldMutation.Record _ -> recordSql
                | TombstoneHoldMutation.Release _ -> releaseSql

            use command = new NpgsqlCommand(sql, connection, transaction)
            bind command context change stored canonical intent instant
            let! inserted = command.ExecuteNonQueryAsync()

            if inserted <> 1 then
                raise (InvalidDataException("Tombstone hold event did not persist."))

            use tip = new NpgsqlCommand(tipSql, connection, transaction)
            Sql.integer tip "next" (stored.AuthorityRevision + 1L)
            Sql.integer tip "previousRevision" stored.AuthorityRevision
            Sql.uuid tip "case" change.CaseId
            Sql.add tip "previousHash" NpgsqlDbType.Bytea (box stored.AuthorityHash)

            Sql.add
                tip
                "hash"
                NpgsqlDbType.Bytea
                (box (CaseTombstoneCandidate.eventHash stored.AuthorityHash canonical))

            let! updated = tip.ExecuteNonQueryAsync()

            if updated <> 1 then
                raise (InvalidDataException("Tombstone authority tip did not advance."))
        }
