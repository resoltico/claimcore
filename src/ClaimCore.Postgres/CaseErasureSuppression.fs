namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Application

/// Neutral pre-disclosure checks use only owner-keyed commitments or an opaque case ID.
module internal CaseErasureSuppression =
    let private exists connection transaction sql bind (ct: CancellationToken) =
        task {
            use command = new NpgsqlCommand(sql, connection, transaction)
            bind command
            let! result = command.ExecuteScalarAsync(ct)
            return result :?> bool
        }

    let referenceBlocked
        connection
        transaction
        (commitments: ISuppressionCommitments)
        reference
        ct
        =
        task {
            commitments.Admit()
            let digest = commitments.Reference reference

            if digest.Length <> 32 then
                invalidOp "Suppression reference commitment is invalid."

            return!
                exists
                    connection
                    transaction
                    ("SELECT EXISTS (SELECT 1 FROM claimcore.case_erasure_tombstones "
                     + "WHERE suppression_key_id=@key AND reference_commitment=@digest)")
                    (fun command ->
                        Sql.uuid command "key" commitments.KeyId
                        Sql.add command "digest" NpgsqlDbType.Bytea (box digest))
                    ct
        }

    let operationBlocked
        connection
        transaction
        (commitments: ISuppressionCommitments)
        operationId
        ct
        =
        task {
            commitments.Admit()
            let digest = commitments.Operation operationId

            if digest.Length <> 32 then
                invalidOp "Suppression operation commitment is invalid."

            return!
                exists
                    connection
                    transaction
                    ("SELECT EXISTS (SELECT 1 FROM claimcore.case_erasure_operation_denials "
                     + "WHERE operation_commitment=@digest)")
                    (fun command -> Sql.add command "digest" NpgsqlDbType.Bytea (box digest))
                    ct
        }

    let caseBlocked connection transaction caseId ct =
        exists
            connection
            transaction
            "SELECT EXISTS (SELECT 1 FROM claimcore.case_erasure_tombstones WHERE case_id=@case)"
            (fun command -> Sql.uuid command "case" caseId)
            ct
