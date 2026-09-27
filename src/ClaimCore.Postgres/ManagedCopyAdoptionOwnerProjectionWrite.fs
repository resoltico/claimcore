namespace ClaimCore.Postgres

open System
open Npgsql
open NpgsqlTypes
open ClaimCore.Application

/// Product exports retain their original producer and REGISTER evidence. Only the copy
/// projection's revision/state/hash advance; effective custody lives in the ADOPT receipt.
module internal ManagedCopyAdoptionOwnerProjectionWrite =
    let private one (command: NpgsqlCommand) =
        task {
            let! count = command.ExecuteNonQueryAsync()

            if count <> 1 then
                invalidOp "Owner copy adoption did not co-commit one projection row."
        }

    let private product connection transaction (request: CopyAdoptionApprovalRequest) eventHash =
        task {
            use command =
                new NpgsqlCommand(
                    "UPDATE claimcore.managed_copies SET state='UNVERIFIED',revision=2,"
                    + "event_hash=@hash,retain_until=@retain "
                    + "WHERE copy_id=@copy AND source_case_id=@case "
                    + "AND producer_kind='PRODUCT_EXPORT' AND state='UNKNOWN' AND revision=1",
                    connection,
                    transaction
                )

            Sql.add command "hash" NpgsqlDbType.Bytea (box eventHash)
            Sql.add command "retain" NpgsqlDbType.TimestampTz (box request.RetainUntil)
            Sql.uuid command "copy" request.CopyId
            Sql.uuid command "case" request.CaseId
            do! one command
        }

    let private externalCopy
        connection
        transaction
        (witness: WitnessProtocol)
        (request: CopyAdoptionApprovalRequest)
        (custody: CopyAdoptionCustody)
        eventHash
        sequence
        cutoffHash
        =
        task {
            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.managed_copies "
                    + "(copy_id,installation_id,lineage_id,witness_epoch,producer_kind,"
                    + "cluster_name,copy_kind,source_case_id,witness_cutoff_sequence,"
                    + "witness_cutoff_hash,ciphertext_sha256,ciphertext_bytes,encryption_key_id,"
                    + "signing_key_id,custodian_commitment,location_commitment,captured_at,"
                    + "retain_until,state,revision,event_hash) VALUES "
                    + "(@copy,@installation,@lineage,@epoch,'ADOPTED_EXTERNAL','NONE','EXPORT',"
                    + "@case,@cutoff,@cutoffHash,@sha,@bytes,@encryption,@signer,@custodian,"
                    + "@location,@captured,@retain,'UNKNOWN',1,@hash)",
                    connection,
                    transaction
                )

            Sql.uuid command "copy" request.CopyId
            Sql.uuid command "installation" witness.Identity.InstallationId
            Sql.uuid command "lineage" witness.Identity.LineageId
            Sql.integer command "epoch" witness.Identity.Epoch
            Sql.uuid command "case" request.CaseId
            Sql.integer command "cutoff" sequence
            Sql.add command "cutoffHash" NpgsqlDbType.Bytea (box cutoffHash)
            Sql.add command "sha" NpgsqlDbType.Bytea (box request.CiphertextSha256)
            Sql.integer command "bytes" request.CiphertextBytes
            Sql.uuid command "encryption" custody.EncryptionKeyId
            Sql.uuid command "signer" request.CustodianSigningKeyId
            Sql.add command "custodian" NpgsqlDbType.Bytea (box request.CustodianCommitment)
            Sql.add command "location" NpgsqlDbType.Bytea (box request.LocationCommitment)
            Sql.add command "captured" NpgsqlDbType.TimestampTz (box request.CapturedAt)
            Sql.add command "retain" NpgsqlDbType.TimestampTz (box request.RetainUntil)
            Sql.add command "hash" NpgsqlDbType.Bytea (box eventHash)
            do! one command
        }

    let apply connection transaction witness request custody eventHash =
        match request.Origin with
        | CopyAdoptionOrigin.ProductExport _ -> product connection transaction request eventHash
        | CopyAdoptionOrigin.AdoptedExternal(sequence, hash) ->
            externalCopy connection transaction witness request custody eventHash sequence hash

    let event
        connection
        transaction
        (request: CopyAdoptionApprovalRequest)
        (custody: CopyAdoptionCustody)
        (submission: CopyAdoptionSubmission)
        previousHash
        eventHash
        (intent: WitnessIntent)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.managed_copy_events "
                    + "(event_id,copy_id,revision,event_kind,producer_kind,canonical_attestation,"
                    + "signing_key_id,ed25519_signature,candidate_sha256,previous_hash,event_hash,"
                    + "witness_sequence,witness_epoch,witness_entry_hash) VALUES "
                    + "(@event,@copy,@revision,@kind,@producer,@canonical,@key,@signature,"
                    + "@candidate,@previous,@hash,@sequence,@epoch,@entryHash)",
                    connection,
                    transaction
                )

            Sql.uuid command "event" submission.AdoptionEventId
            Sql.uuid command "copy" request.CopyId
            Sql.integer command "revision" custody.Revision
            Sql.text command "kind" custody.EventKind
            Sql.text command "producer" custody.ProducerKind
            Sql.add command "canonical" NpgsqlDbType.Bytea (box submission.Custodian.Canonical)
            Sql.uuid command "key" request.CustodianSigningKeyId
            Sql.add command "signature" NpgsqlDbType.Bytea (box submission.Custodian.Signature)
            Sql.add command "candidate" NpgsqlDbType.Bytea (box intent.CandidateHash)
            Sql.add command "previous" NpgsqlDbType.Bytea (box previousHash)
            Sql.add command "hash" NpgsqlDbType.Bytea (box eventHash)
            Sql.integer command "sequence" intent.Ticket.Sequence
            Sql.integer command "epoch" intent.Ticket.Epoch
            Sql.add command "entryHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
            do! one command
        }
