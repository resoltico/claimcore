namespace ClaimCore.Postgres

open System
open Npgsql
open NpgsqlTypes

/// The owner co-commits RETAINED projection, signed VERIFY event and physical report receipt.
module internal ManagedCopyVerifiedRestoreWrite =
    let private receiptSql =
        "INSERT INTO claimcore.managed_copy_verifications "
        + "(verification_event_id,copy_id,copy_revision,verifier_signing_key_id,"
        + "verifier_holder_actor_id,nonce,canonical_report,ed25519_signature,"
        + "report_sha256,archive_object_id,cluster_name,copy_kind,location_commitment,"
        + "ciphertext_sha256,ciphertext_bytes,decrypted_sha256,decrypted_bytes,"
        + "postgres_system_id,timeline,backup_manifest_sha256,wal_segment,"
        + "recovered_row_count,wal_segment_bytes,checked_at,valid_until,"
        + "witness_cutoff_sequence,witness_cutoff_hash) VALUES "
        + "(@event,@copy,@revision,@key,@holder,@nonce,@canonical,@signature,@report,"
        + "@object,@cluster,@kind,@location,@ciphertext,@ciphertextBytes,"
        + "@decrypted,@decryptedBytes,@system,@timeline,@manifest,@segment,@rows,"
        + "@segmentBytes,@checked,@valid,@cutoff,@cutoffHash)"

    let private projection
        (connection: NpgsqlConnection)
        transaction
        (transition: ManagedCopyTransition)
        proofHash
        observed
        eventHash
        =
        task {
            use command =
                new NpgsqlCommand(
                    "UPDATE claimcore.managed_copies SET state='RETAINED',revision=@revision,"
                    + "event_hash=@hash,verification_proof_sha256=@proof,last_verified_at=@observed "
                    + "WHERE copy_id=@copy AND state='UNVERIFIED' AND revision=@previous "
                    + "AND verification_proof_sha256 IS NULL",
                    connection,
                    transaction
                )

            Sql.integer command "revision" transition.Revision
            Sql.add command "hash" NpgsqlDbType.Bytea (box eventHash)
            Sql.add command "proof" NpgsqlDbType.Bytea (box proofHash)
            Sql.add command "observed" NpgsqlDbType.TimestampTz (box observed)
            Sql.uuid command "copy" transition.Copy.CopyId
            Sql.integer command "previous" (transition.Revision - 1L)
            let! changed = command.ExecuteNonQueryAsync()

            if changed <> 1 then
                invalidOp "Physical VERIFY projection was not co-committed."
        }

    let private bindReceiptIdentity
        (command: NpgsqlCommand)
        (proof: ManagedCopyPhysicalProof)
        canonical
        signature
        reportHash
        =
        Sql.uuid command "event" proof.VerificationEventId
        Sql.uuid command "copy" proof.CopyId
        Sql.integer command "revision" proof.CopyRevision
        Sql.uuid command "key" proof.VerifierSigningKeyId
        Sql.uuid command "holder" proof.VerifierHolderActorId
        Sql.add command "nonce" NpgsqlDbType.Bytea (box proof.Nonce)
        Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
        Sql.add command "signature" NpgsqlDbType.Bytea (box signature)
        Sql.add command "report" NpgsqlDbType.Bytea (box reportHash)
        Sql.uuid command "object" proof.ArchiveObjectId
        Sql.text command "cluster" proof.Cluster
        Sql.text command "kind" proof.Kind

    let private bindReceiptObject (command: NpgsqlCommand) (proof: ManagedCopyPhysicalProof) =
        Sql.add command "location" NpgsqlDbType.Bytea (box proof.LocationCommitment)
        Sql.add command "ciphertext" NpgsqlDbType.Bytea (box proof.CiphertextSha256)
        Sql.integer command "ciphertextBytes" proof.CiphertextBytes
        Sql.add command "decrypted" NpgsqlDbType.Bytea (box proof.DecryptedSha256)
        Sql.integer command "decryptedBytes" proof.DecryptedBytes
        Sql.text command "system" proof.PostgresSystemId
        Sql.add command "timeline" NpgsqlDbType.Integer (box proof.Timeline)

        Sql.add
            command
            "manifest"
            NpgsqlDbType.Bytea
            (proof.BackupManifestSha256 |> Option.map box |> Option.defaultValue DBNull.Value)

        Sql.add
            command
            "segment"
            NpgsqlDbType.Text
            (proof.WalSegment |> Option.map box |> Option.defaultValue DBNull.Value)

        Sql.add
            command
            "rows"
            NpgsqlDbType.Bigint
            (proof.RecoveredRowCount |> Option.map box |> Option.defaultValue DBNull.Value)

        Sql.add command "segmentBytes" NpgsqlDbType.Integer (box proof.WalSegmentBytes)

    let private bindReceiptTime (command: NpgsqlCommand) (proof: ManagedCopyPhysicalProof) =
        Sql.add command "checked" NpgsqlDbType.TimestampTz (box proof.CheckedAt)
        Sql.add command "valid" NpgsqlDbType.TimestampTz (box proof.ValidUntil)
        Sql.integer command "cutoff" proof.WitnessCutoffSequence
        Sql.add command "cutoffHash" NpgsqlDbType.Bytea (box proof.WitnessCutoffHash)

    let private receipt
        (connection: NpgsqlConnection)
        transaction
        (proof: ManagedCopyPhysicalProof)
        canonical
        signature
        reportHash
        =
        task {
            use command = new NpgsqlCommand(receiptSql, connection, transaction)
            bindReceiptIdentity command proof canonical signature reportHash
            bindReceiptObject command proof
            bindReceiptTime command proof
            let! inserted = command.ExecuteNonQueryAsync()

            if inserted <> 1 then
                invalidOp "Physical copy proof was not retained."
        }

    let apply
        connection
        transaction
        (transition: ManagedCopyTransition)
        canonical
        signature
        (verified: VerifiedManagedCopyRestore)
        eventHash
        (intent: WitnessIntent)
        =
        task {
            let proof = verified.Proof

            do!
                projection
                    connection
                    transaction
                    transition
                    verified.ReportSha256
                    proof.CheckedAt
                    eventHash

            do!
                ManagedCopyTransitionAdministration.insertEvent
                    connection
                    transaction
                    transition
                    canonical
                    signature
                    eventHash
                    intent

            do!
                receipt
                    connection
                    transaction
                    proof
                    verified.Canonical
                    verified.Signature
                    verified.ReportSha256
        }
