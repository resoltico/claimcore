namespace ClaimCore.Postgres

open System
open System.IO
open Npgsql

/// Owner-only deletion of claimant-bearing live projections and histories. Export receipts,
/// signed copy inventory, authority history and keyed tombstones remain pseudonymous evidence.
module internal CaseErasurePurgeDelete =
    let private delete connection transaction caseId sql =
        task {
            use command = new NpgsqlCommand(sql, connection, transaction)
            Sql.uuid command "case" caseId
            let! deleted = command.ExecuteNonQueryAsync()

            if deleted < 0 then
                raise (InvalidDataException("Erasure deletion count is invalid."))

            return int64 deleted
        }

    let private deleteOperational connection transaction caseId =
        task {
            let! exportPayloads =
                delete
                    connection
                    transaction
                    caseId
                    ("DELETE FROM claimcore.recovery_artifact_payloads p USING "
                     + "claimcore.recovery_artifact_exports e "
                     + "WHERE p.export_id=e.export_id AND e.case_id=@case")

            let! preparations =
                delete
                    connection
                    transaction
                    caseId
                    "DELETE FROM claimcore.request_preparations WHERE case_id=@case"

            let! revocations =
                delete
                    connection
                    transaction
                    caseId
                    "DELETE FROM claimcore.operation_revocations WHERE case_id=@case"

            return exportPayloads + preparations + revocations
        }

    let private deleteCaseHistory connection transaction caseId =
        task {
            let! approvals =
                delete
                    connection
                    transaction
                    caseId
                    "DELETE FROM claimcore.case_lifecycle_approvals WHERE case_id=@case"

            let! events =
                delete
                    connection
                    transaction
                    caseId
                    "DELETE FROM claimcore.case_lifecycle_events WHERE case_id=@case"

            let! holds =
                delete
                    connection
                    transaction
                    caseId
                    "DELETE FROM claimcore.case_holds WHERE case_id=@case"

            let! changes =
                delete
                    connection
                    transaction
                    caseId
                    "DELETE FROM claimcore.case_changes WHERE case_id=@case"

            return approvals + events + holds + changes
        }

    let verifyAbsent connection transaction caseId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT NOT EXISTS(SELECT 1 FROM claimcore.cases WHERE case_id=@case) "
                    + "AND NOT EXISTS(SELECT 1 FROM claimcore.case_changes WHERE case_id=@case) "
                    + "AND NOT EXISTS(SELECT 1 FROM claimcore.case_lifecycle_events WHERE case_id=@case) "
                    + "AND NOT EXISTS(SELECT 1 FROM claimcore.case_lifecycle_approvals WHERE case_id=@case) "
                    + "AND NOT EXISTS(SELECT 1 FROM claimcore.case_holds WHERE case_id=@case) "
                    + "AND NOT EXISTS(SELECT 1 FROM claimcore.request_preparations WHERE case_id=@case) "
                    + "AND NOT EXISTS(SELECT 1 FROM claimcore.operation_revocations WHERE case_id=@case) "
                    + "AND NOT EXISTS(SELECT 1 FROM claimcore.recovery_artifact_payloads p "
                    + "JOIN claimcore.recovery_artifact_exports e ON e.export_id=p.export_id "
                    + "WHERE e.case_id=@case)",
                    connection,
                    transaction
                )

            Sql.uuid command "case" caseId
            let! result = command.ExecuteScalarAsync()

            if not (unbox<bool> result) then
                raise (InvalidDataException("Live claimant payload remains after purge."))
        }

    let purge (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) caseId =
        task {
            let! operational = deleteOperational connection transaction caseId
            let! history = deleteCaseHistory connection transaction caseId

            let! currentCount =
                delete
                    connection
                    transaction
                    caseId
                    "DELETE FROM claimcore.cases WHERE case_id=@case AND privacy_phase='ERASURE_PENDING'"

            if currentCount <> 1L then
                raise (InvalidDataException("Live case row was not removed exactly once."))

            do! verifyAbsent connection transaction caseId
            return operational + history + currentCount
        }
