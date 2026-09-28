namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat

module internal RecoveryArtifactExportAdmission =
    let activeCase connection transaction (retained: RetainedPreparation) (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT disposition,privacy_phase FROM claimcore.cases "
                    + "WHERE case_id=@case FOR UPDATE",
                    connection,
                    transaction
                )

            Sql.uuid command "case" retained.CaseId
            use! reader = command.ExecuteReaderAsync(ct)
            let! found = reader.ReadAsync(ct)

            if found then
                return reader.GetString(0) = "ACTIVE" && reader.GetString(1) = "ACTIVE"
            else
                return
                    match RequestRecord.decode 65536 retained.CanonicalRequest with
                    | Ok request when request.ExpectedVersion = 0L ->
                        match request.Command with
                        | Command.Open _ -> true
                        | _ -> false
                    | _ -> false
        }

    let notRevoked connection transaction operationId (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT NOT EXISTS (SELECT 1 FROM claimcore.operation_revocations "
                    + "WHERE operation_id=@operation)",
                    connection,
                    transaction
                )

            Sql.uuid command "operation" operationId
            let! result = command.ExecuteScalarAsync(ct)
            return result :?> bool
        }

    let retainedExact
        connection
        transaction
        (retained: RetainedPreparation)
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT case_id,preparer_actor_id,preparer_grant_revision,importer_actor_id,"
                    + "canonical_request FROM claimcore.request_preparations "
                    + "WHERE operation_id=@operation",
                    connection,
                    transaction
                )

            Sql.uuid command "operation" retained.OperationId
            use! reader = command.ExecuteReaderAsync(ct)
            let! found = reader.ReadAsync(ct)

            return
                found
                && reader.GetGuid(0) = retained.CaseId
                && reader.GetGuid(1) = retained.PreparerActorId
                && reader.GetInt64(2) = retained.PreparerGrantRevision
                && (if reader.IsDBNull(3) then None else Some(reader.GetGuid(3))) =
                    retained.ImporterActorId
                && CryptographicOperations.FixedTimeEquals(
                    reader.GetFieldValue<byte array>(4),
                    retained.CanonicalRequest
                )
        }

    let countForKey connection transaction keyId (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT count(*) FROM claimcore.recovery_artifact_exports WHERE key_id=@key",
                    connection,
                    transaction
                )

            Sql.uuid command "key" keyId
            let! value = command.ExecuteScalarAsync(ct)
            return Convert.ToInt32(value)
        }
