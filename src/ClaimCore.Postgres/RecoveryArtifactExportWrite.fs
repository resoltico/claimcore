namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open NpgsqlTypes

module internal RecoveryArtifactExportWrite =
    let private insertSql =
        "INSERT INTO claimcore.recovery_artifact_exports "
        + "(export_id,installation_id,witness_epoch,case_id,operation_id,"
        + "preparer_actor_id,preparer_grant_revision,importer_actor_id,"
        + "exporter_actor_id,exporter_grant_revision,key_id,nonce,issued_at,expires_at,"
        + "artifact_sha256,key_issuance_ordinal,key_max_exports,"
        + "witness_sequence,witness_entry_hash,witness_candidate_sha256) "
        + "VALUES (@export,@installation,@epoch,@case,@operation,@preparer,@preparerRevision,"
        + "@importer,@exporter,@exporterRevision,@key,@nonce,@issued,@expires,"
        + "@sha,@ordinal,@maximum,@sequence,@entryHash,@digest)"

    let private payloadSql =
        "INSERT INTO claimcore.recovery_artifact_payloads "
        + "(export_id,artifact_bytes,canonical_action) VALUES (@export,@bytes,@canonical)"

    let private bindIdentity (command: NpgsqlCommand) (evidence: RecoveryArtifactExportEvidence) =
        let artifact = evidence.Artifact
        Sql.uuid command "export" evidence.ExportId
        Sql.uuid command "installation" artifact.InstallationId
        Sql.integer command "epoch" artifact.Epoch
        Sql.uuid command "case" artifact.CaseId
        Sql.uuid command "operation" artifact.OperationId
        Sql.uuid command "preparer" artifact.PreparerActorId
        Sql.integer command "preparerRevision" artifact.PreparerGrantRevision
        Sql.optional command "importer" NpgsqlDbType.Uuid artifact.ImporterActorId
        Sql.uuid command "exporter" evidence.ExporterActorId
        Sql.integer command "exporterRevision" evidence.ExporterGrantRevision
        Sql.uuid command "key" artifact.KeyId
        Sql.add command "nonce" NpgsqlDbType.Bytea (box artifact.Nonce)
        Sql.add command "issued" NpgsqlDbType.TimestampTz (box artifact.IssuedAt)
        Sql.add command "expires" NpgsqlDbType.TimestampTz (box artifact.ExpiresAt)

    let insert
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (evidence: RecoveryArtifactExportEvidence)
        (canonical: byte array)
        (intent: WitnessIntent)
        (ct: CancellationToken)
        =
        task {
            use command = new NpgsqlCommand(insertSql, connection, transaction)
            bindIdentity command evidence

            Sql.add
                command
                "sha"
                NpgsqlDbType.Bytea
                (box (Security.Cryptography.SHA256.HashData(evidence.ArtifactBytes)))

            Sql.add command "ordinal" NpgsqlDbType.Integer (box evidence.KeyIssuanceOrdinal)
            Sql.add command "maximum" NpgsqlDbType.Integer (box evidence.KeyMaximumExports)
            Sql.integer command "sequence" intent.Ticket.Sequence
            Sql.add command "entryHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
            Sql.add command "digest" NpgsqlDbType.Bytea (box intent.CandidateHash)
            let! affected = command.ExecuteNonQueryAsync(ct)

            if affected <> 1 then
                invalidOp "Recovery export inventory insert was incomplete."

            use payload = new NpgsqlCommand(payloadSql, connection, transaction)
            Sql.uuid payload "export" evidence.ExportId
            Sql.add payload "bytes" NpgsqlDbType.Bytea (box evidence.ArtifactBytes)
            Sql.add payload "canonical" NpgsqlDbType.Bytea (box canonical)
            let! payloadCount = payload.ExecuteNonQueryAsync(ct)

            if payloadCount <> 1 then
                invalidOp "Recovery export encrypted payload insert was incomplete."
        }
