namespace ClaimCore.Postgres

open System
open Npgsql
open NpgsqlTypes

/// Schema-owner storage of externally signed evidence after registered-key verification.
module internal ManagedCopyOwnerWrite =
    let private one (command: NpgsqlCommand) =
        task {
            let! count = command.ExecuteNonQueryAsync()

            if count <> 1 then
                invalidOp "Managed-copy owner write was incomplete."
        }

    let insertCopy
        (connection: NpgsqlConnection)
        transaction
        (value: ManagedCopyAttestation)
        eventHash
        =
        task {
            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.managed_copies "
                    + "(copy_id,installation_id,lineage_id,witness_epoch,producer_kind,"
                    + "cluster_name,copy_kind,source_case_id,postgres_system_id,timeline,wal_segment_bytes,"
                    + "backup_manifest_sha256,wal_start_lsn,wal_end_lsn,wal_segment,"
                    + "witness_cutoff_sequence,witness_cutoff_hash,ciphertext_sha256,"
                    + "ciphertext_bytes,encryption_key_id,signing_key_id,custodian_commitment,"
                    + "location_commitment,captured_at,retain_until,state,revision,event_hash) "
                    + "VALUES (@copy,@installation,@lineage,@epoch,'OWNER_ATTESTED',"
                    + "@cluster,@kind,@sourceCase,@system,@timeline,@segmentBytes,@manifest,@startLsn,"
                    + "@endLsn,@segment,@cutoff,@cutoffHash,@sha,@bytes,@encryptionKey,"
                    + "@signingKey,@custodian,@location,@captured,@retained,'UNVERIFIED',1,@eventHash)",
                    connection,
                    transaction
                )

            Sql.uuid command "copy" value.CopyId
            Sql.uuid command "installation" value.InstallationId
            Sql.uuid command "lineage" value.LineageId
            Sql.integer command "epoch" value.Epoch
            Sql.text command "cluster" value.Cluster
            Sql.text command "kind" value.Kind
            Sql.optional command "sourceCase" NpgsqlDbType.Uuid value.SourceCaseId
            Sql.optional command "system" NpgsqlDbType.Text value.PostgresSystemId
            Sql.optional command "timeline" NpgsqlDbType.Integer value.Timeline
            Sql.optional command "segmentBytes" NpgsqlDbType.Integer value.WalSegmentBytes
            Sql.optional command "manifest" NpgsqlDbType.Bytea value.BackupManifestSha256
            Sql.optional command "startLsn" NpgsqlDbType.Text value.WalStartLsn
            Sql.optional command "endLsn" NpgsqlDbType.Text value.WalEndLsn
            Sql.optional command "segment" NpgsqlDbType.Text value.WalSegment
            Sql.integer command "cutoff" value.WitnessCutoffSequence
            Sql.add command "cutoffHash" NpgsqlDbType.Bytea (box value.WitnessCutoffHash)
            Sql.add command "sha" NpgsqlDbType.Bytea (box value.CiphertextSha256)
            Sql.integer command "bytes" value.CiphertextBytes
            Sql.uuid command "encryptionKey" value.EncryptionKeyId
            Sql.uuid command "signingKey" value.SigningKeyId
            Sql.add command "custodian" NpgsqlDbType.Bytea (box value.CustodianCommitment)
            Sql.add command "location" NpgsqlDbType.Bytea (box value.LocationCommitment)
            Sql.add command "captured" NpgsqlDbType.TimestampTz (box value.CapturedAt)
            Sql.add command "retained" NpgsqlDbType.TimestampTz (box value.RetainUntil)
            Sql.add command "eventHash" NpgsqlDbType.Bytea (box eventHash)
            do! one command
        }

    let insertEvent
        (connection: NpgsqlConnection)
        transaction
        (value: ManagedCopyAttestation)
        (canonical: byte array)
        (signature: byte array)
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
                    + "witness_sequence,witness_epoch,witness_entry_hash) "
                    + "VALUES (@event,@copy,1,'REGISTER','OWNER_ATTESTED',@canonical,"
                    + "@key,@signature,@candidate,@previous,@eventHash,@sequence,@epoch,@entryHash)",
                    connection,
                    transaction
                )

            Sql.uuid command "event" value.EventId
            Sql.uuid command "copy" value.CopyId
            Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
            Sql.uuid command "key" value.SigningKeyId
            Sql.add command "signature" NpgsqlDbType.Bytea (box signature)
            Sql.add command "candidate" NpgsqlDbType.Bytea (box intent.CandidateHash)
            Sql.add command "previous" NpgsqlDbType.Bytea (box previousHash)
            Sql.add command "eventHash" NpgsqlDbType.Bytea (box eventHash)
            Sql.integer command "sequence" intent.Ticket.Sequence
            Sql.integer command "epoch" intent.Ticket.Epoch
            Sql.add command "entryHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
            do! one command
        }
