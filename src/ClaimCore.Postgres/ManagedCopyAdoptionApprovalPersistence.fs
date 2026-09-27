namespace ClaimCore.Postgres

open System
open System.IO
open Npgsql
open NpgsqlTypes
open ClaimCore.Application

module internal ManagedCopyAdoptionApprovalPersistence =
    let private sql =
        "INSERT INTO claimcore.managed_copy_adoption_approvals "
        + "(approval_id,adoption_event_id,copy_id,case_id,origin_kind,export_id,"
        + "ciphertext_sha256,ciphertext_bytes,pre_fence_kind,pre_fence_sequence,"
        + "pre_fence_hash,location_commitment,retain_until,owner_actor_id,"
        + "owner_grant_revision,approved_at,expires_at,canonical_action,candidate_sha256,"
        + "witness_sequence,witness_epoch,witness_entry_hash) "
        + "VALUES (@approval,@event,@copy,@case,@origin,@export,@sha,@bytes,@preKind,"
        + "@preSequence,@preHash,@location,@retain,@actor,@grant,@instant,@expires,"
        + "@canonical,@candidate,@sequence,@epoch,@entryHash)"

    let private origin (command: NpgsqlCommand) =
        function
        | CopyAdoptionOrigin.ProductExport(exportId, sequence, hash) ->
            Sql.text command "origin" "PRODUCT_EXPORT"
            Sql.uuid command "export" exportId
            Sql.text command "preKind" "PRODUCT_EXPORT_RECEIPT"
            Sql.integer command "preSequence" sequence
            Sql.add command "preHash" NpgsqlDbType.Bytea (box hash)
        | CopyAdoptionOrigin.AdoptedExternal(sequence, hash) ->
            Sql.text command "origin" "ADOPTED_EXTERNAL"
            Sql.add command "export" NpgsqlDbType.Uuid (box DBNull.Value)
            Sql.text command "preKind" "PUBLISHED_REGISTRY"
            Sql.integer command "preSequence" sequence
            Sql.add command "preHash" NpgsqlDbType.Bytea (box hash)

    let persist
        connection
        transaction
        (request: CopyAdoptionApprovalRequest)
        (context: ActorCallContext)
        (approvedAt: DateTimeOffset)
        (canonical: byte array)
        (intent: WitnessIntent)
        =
        task {
            use command = new NpgsqlCommand(sql, connection, transaction)
            Sql.uuid command "approval" request.ApprovalId
            Sql.uuid command "event" request.AdoptionEventId
            Sql.uuid command "copy" request.CopyId
            Sql.uuid command "case" request.CaseId
            origin command request.Origin
            Sql.add command "sha" NpgsqlDbType.Bytea (box request.CiphertextSha256)
            Sql.integer command "bytes" request.CiphertextBytes
            Sql.add command "location" NpgsqlDbType.Bytea (box request.LocationCommitment)
            Sql.add command "retain" NpgsqlDbType.TimestampTz (box request.RetainUntil)
            Sql.uuid command "actor" context.Binding.ActorId
            Sql.integer command "grant" context.Binding.GrantRevision
            Sql.add command "instant" NpgsqlDbType.TimestampTz (box approvedAt)
            Sql.add command "expires" NpgsqlDbType.TimestampTz (box request.ExpiresAt)
            Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
            Sql.add command "candidate" NpgsqlDbType.Bytea (box intent.CandidateHash)
            Sql.integer command "sequence" intent.Ticket.Sequence
            Sql.integer command "epoch" intent.Ticket.Epoch
            Sql.add command "entryHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
            let! inserted = command.ExecuteNonQueryAsync()

            if inserted <> 1 then
                raise (InvalidDataException("Copy adoption owner approval did not persist."))
        }
