namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open NpgsqlTypes

/// Co-committed export inventory: registration is not proof of external deletion or retention.
module internal ManagedCopyExports =
    let private copySql =
        "INSERT INTO claimcore.managed_copies "
        + "(copy_id,installation_id,lineage_id,witness_epoch,producer_kind,cluster_name,copy_kind,"
        + "source_case_id,witness_cutoff_sequence,witness_cutoff_hash,ciphertext_sha256,ciphertext_bytes,"
        + "encryption_key_id,captured_at,retain_until,state,revision,event_hash,product_export_id) "
        + "VALUES (@export,@installation,@lineage,@epoch,'PRODUCT_EXPORT','NONE','EXPORT',"
        + "@case,NULL,NULL,@sha,@bytes,@key,@issued,@expires,'UNKNOWN',1,@hash,@export)"

    let private eventSql =
        "INSERT INTO claimcore.managed_copy_events "
        + "(event_id,copy_id,revision,event_kind,producer_kind,canonical_attestation,signing_key_id,"
        + "ed25519_signature,candidate_sha256,previous_hash,event_hash,witness_sequence,witness_epoch,"
        + "witness_entry_hash) VALUES (@export,@export,1,'REGISTER','PRODUCT_EXPORT',@canonical,"
        + "NULL,NULL,@digest,@previous,@hash,@sequence,@epoch,@entryHash)"

    let private insertCopy
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (exportId: Guid)
        (caseId: Guid)
        (keyId: Guid)
        (issuedAt: DateTimeOffset)
        (expiresAt: DateTimeOffset)
        (sha: byte[])
        (bytes: int)
        (hash: byte[])
        (ct: CancellationToken)
        =
        task {
            use command = new NpgsqlCommand(copySql, connection, transaction)
            let identity = witness.Identity
            Sql.uuid command "export" exportId
            Sql.uuid command "installation" identity.InstallationId
            Sql.uuid command "lineage" identity.LineageId
            Sql.integer command "epoch" identity.Epoch
            Sql.uuid command "case" caseId
            Sql.add command "sha" NpgsqlDbType.Bytea (box sha)
            Sql.integer command "bytes" (int64 bytes)
            Sql.uuid command "key" keyId
            Sql.add command "issued" NpgsqlDbType.TimestampTz (box issuedAt)
            Sql.add command "expires" NpgsqlDbType.TimestampTz (box expiresAt)
            Sql.add command "hash" NpgsqlDbType.Bytea (box hash)
            let! count = command.ExecuteNonQueryAsync(ct)

            if count <> 1 then
                invalidOp "Managed export copy registration was incomplete."
        }

    let private insertEvent
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (exportId: Guid)
        (canonical: byte[])
        (intent: WitnessIntent)
        (previous: byte[])
        (hash: byte[])
        (ct: CancellationToken)
        =
        task {
            use command = new NpgsqlCommand(eventSql, connection, transaction)
            Sql.uuid command "export" exportId
            Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
            Sql.add command "digest" NpgsqlDbType.Bytea (box intent.CandidateHash)
            Sql.add command "previous" NpgsqlDbType.Bytea (box previous)
            Sql.add command "hash" NpgsqlDbType.Bytea (box hash)
            Sql.integer command "sequence" intent.Ticket.Sequence
            Sql.integer command "epoch" intent.Ticket.Epoch
            Sql.add command "entryHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
            let! count = command.ExecuteNonQueryAsync(ct)

            if count <> 1 then
                invalidOp "Managed export event registration was incomplete."
        }

    let private validate
        exportId
        caseId
        keyId
        issuedAt
        expiresAt
        (sha: byte[])
        bytes
        (canonical: byte[])
        (intent: WitnessIntent)
        (witness: WitnessProtocol)
        =
        if
            exportId = Guid.Empty
            || caseId = Guid.Empty
            || keyId = Guid.Empty
            || issuedAt >= expiresAt
            || sha.Length <> 32
            || bytes < 1
            || bytes > 200704
            || canonical.Length < 1
            || canonical.Length > 300000
            || intent.Ticket.Epoch <> witness.Identity.Epoch
            || not (
                CryptographicOperations.FixedTimeEquals(
                    ReadOnlySpan<byte>(SHA256.HashData(canonical)),
                    ReadOnlySpan<byte>(intent.CandidateHash)
                )
            )
        then
            invalidArg (nameof canonical) "Managed export authority evidence is invalid."

    let register
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (exportId: Guid)
        (caseId: Guid)
        (keyId: Guid)
        (issuedAt: DateTimeOffset)
        (expiresAt: DateTimeOffset)
        (artifactSha256: byte[])
        (artifactBytesLength: int)
        (canonicalAction: byte[])
        (intent: WitnessIntent)
        (ct: CancellationToken)
        =
        task {
            validate
                exportId
                caseId
                keyId
                issuedAt
                expiresAt
                artifactSha256
                artifactBytesLength
                canonicalAction
                intent
                witness

            let previous = Array.zeroCreate<byte> 32
            let hash = ManagedCopyEventHash.compute previous canonicalAction None

            do!
                insertCopy
                    connection
                    transaction
                    witness
                    exportId
                    caseId
                    keyId
                    issuedAt
                    expiresAt
                    artifactSha256
                    artifactBytesLength
                    hash
                    ct

            do! insertEvent connection transaction exportId canonicalAction intent previous hash ct
        }
