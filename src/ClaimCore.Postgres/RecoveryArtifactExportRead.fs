namespace ClaimCore.Postgres

open System
open System.Data.Common
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.RecordFormat
open WitnessProtocolReconciliation

[<NoEquality; NoComparison>]
type internal RecoveryArtifactExportRow =
    {
        ExportId: Guid
        OperationId: Guid
        CaseId: Guid
        ExporterActorId: Guid
        ExporterGrantRevision: int64
        KeyId: Guid
        IssuedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
        ArtifactSha256: byte array
        ArtifactBytes: byte array
        CanonicalAction: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessEntryHash: byte array
        WitnessCandidateSha256: byte array
        InstallationId: Guid
        PreparerActorId: Guid
        PreparerGrantRevision: int64
        ImporterActorId: Guid option
        Nonce: byte array
        KeyIssuanceOrdinal: int
        KeyMaximumExports: int
    }

module internal RecoveryArtifactExportRead =
    let private read (reader: DbDataReader) =
        {
            ExportId = reader.GetGuid(0)
            OperationId = reader.GetGuid(1)
            CaseId = reader.GetGuid(2)
            ExporterActorId = reader.GetGuid(3)
            ExporterGrantRevision = reader.GetInt64(4)
            KeyId = reader.GetGuid(5)
            IssuedAt = reader.GetFieldValue<DateTimeOffset>(6)
            ExpiresAt = reader.GetFieldValue<DateTimeOffset>(7)
            ArtifactSha256 = reader.GetFieldValue<byte array>(8)
            ArtifactBytes = reader.GetFieldValue<byte array>(9)
            CanonicalAction = reader.GetFieldValue<byte array>(10)
            WitnessSequence = reader.GetInt64(11)
            WitnessEpoch = reader.GetInt64(12)
            WitnessEntryHash = reader.GetFieldValue<byte array>(13)
            WitnessCandidateSha256 = reader.GetFieldValue<byte array>(14)
            InstallationId = reader.GetGuid(15)
            PreparerActorId = reader.GetGuid(16)
            PreparerGrantRevision = reader.GetInt64(17)
            ImporterActorId =
                if reader.IsDBNull(18) then
                    None
                else
                    Some(reader.GetGuid(18))
            Nonce = reader.GetFieldValue<byte array>(19)
            KeyIssuanceOrdinal = reader.GetInt32(20)
            KeyMaximumExports = reader.GetInt32(21)
        }

    let find
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction option)
        (exportId: Guid)
        (ct: CancellationToken)
        : Task<RecoveryArtifactExportRow option> =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT e.export_id,e.operation_id,e.case_id,e.exporter_actor_id,"
                    + "e.exporter_grant_revision,e.key_id,e.issued_at,e.expires_at,"
                    + "e.artifact_sha256,p.artifact_bytes,p.canonical_action,"
                    + "e.witness_sequence,e.witness_epoch,e.witness_entry_hash,e.witness_candidate_sha256,"
                    + "e.installation_id,e.preparer_actor_id,e.preparer_grant_revision,"
                    + "e.importer_actor_id,e.nonce,e.key_issuance_ordinal,e.key_max_exports "
                    + "FROM claimcore.recovery_artifact_exports e "
                    + "JOIN claimcore.recovery_artifact_payloads p ON p.export_id=e.export_id "
                    + "WHERE e.export_id=@export",
                    connection
                )

            transaction |> Option.iter (fun value -> command.Transaction <- value)
            Sql.uuid command "export" exportId
            use! reader = command.ExecuteReaderAsync(ct)
            let! found = reader.ReadAsync(ct)
            return if found then Some(read reader) else None
        }

    let exact (row: RecoveryArtifactExportRow) (artifact: RecoveryArtifactV3) (source: byte array) =
        row.ExportId = artifact.ExportId
        && row.OperationId = artifact.OperationId
        && row.CaseId = artifact.CaseId
        && row.ExporterActorId = artifact.ExporterActorId
        && row.ExporterGrantRevision = artifact.ExporterGrantRevision
        && row.KeyId = artifact.KeyId
        && row.InstallationId = artifact.InstallationId
        && row.WitnessEpoch = artifact.Epoch
        && row.PreparerActorId = artifact.PreparerActorId
        && row.PreparerGrantRevision = artifact.PreparerGrantRevision
        && row.ImporterActorId = artifact.ImporterActorId
        && row.Nonce = artifact.Nonce
        && row.IssuedAt = artifact.IssuedAt
        && row.ExpiresAt = artifact.ExpiresAt
        && CryptographicOperations.FixedTimeEquals(
            ReadOnlySpan<byte>(row.ArtifactBytes),
            ReadOnlySpan<byte>(source)
        )
        && CryptographicOperations.FixedTimeEquals(
            ReadOnlySpan<byte>(row.ArtifactSha256),
            ReadOnlySpan<byte>(SHA256.HashData(source))
        )
        && CryptographicOperations.FixedTimeEquals(
            ReadOnlySpan<byte>(row.WitnessCandidateSha256),
            ReadOnlySpan<byte>(SHA256.HashData(row.CanonicalAction))
        )
        && RecoveryArtifactExportCandidate.verifyStored
            {
                ExportId = row.ExportId
                Artifact = artifact
                ExporterActorId = row.ExporterActorId
                ExporterGrantRevision = row.ExporterGrantRevision
                ArtifactBytes = source
                KeyIssuanceOrdinal = row.KeyIssuanceOrdinal
                KeyMaximumExports = row.KeyMaximumExports
            }
            row.CanonicalAction

    let managedCopyMatches
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (row: RecoveryArtifactExportRow)
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM claimcore.managed_copies c "
                    + "JOIN claimcore.managed_copy_events e ON e.copy_id=c.copy_id "
                    + "WHERE c.copy_id=@export AND c.product_export_id=@export "
                    + "AND c.source_case_id=@case AND c.ciphertext_sha256=@sha "
                    + "AND c.ciphertext_bytes=@bytes AND e.event_id=@export "
                    + "AND e.canonical_attestation=@canonical AND e.candidate_sha256=@digest "
                    + "AND e.witness_sequence=@sequence AND e.witness_epoch=@epoch "
                    + "AND e.witness_entry_hash=@entryHash)",
                    connection,
                    transaction
                )

            Sql.uuid command "export" row.ExportId
            Sql.uuid command "case" row.CaseId
            Sql.add command "sha" NpgsqlTypes.NpgsqlDbType.Bytea (box row.ArtifactSha256)
            Sql.integer command "bytes" (int64 row.ArtifactBytes.Length)
            Sql.add command "canonical" NpgsqlTypes.NpgsqlDbType.Bytea (box row.CanonicalAction)
            Sql.add command "digest" NpgsqlTypes.NpgsqlDbType.Bytea (box row.WitnessCandidateSha256)
            Sql.integer command "sequence" row.WitnessSequence
            Sql.integer command "epoch" row.WitnessEpoch
            Sql.add command "entryHash" NpgsqlTypes.NpgsqlDbType.Bytea (box row.WitnessEntryHash)
            let! result = command.ExecuteScalarAsync(ct)
            return result :?> bool
        }

    let requireSettled (witness: WitnessProtocol) row =
        witness.VerifyAuthorityEvidence(
            row.ExportId,
            row.WitnessSequence,
            row.WitnessEpoch,
            row.WitnessEntryHash,
            row.WitnessCandidateSha256
        )

    let reconcileSettled (witness: WitnessProtocol) row =
        witness.ReconcileAuthority(
            row.ExportId,
            row.WitnessSequence,
            row.WitnessEpoch,
            row.WitnessEntryHash,
            row.CanonicalAction
        )
