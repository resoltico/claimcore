namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application
open ActorGrantGateQueries
open ActorGrantGateBinding

/// Import admission follows authenticated v3 decode and checks the exact current export/case.
module internal ActorGrantImportGate =
    let private exactExport connection transaction caseId operationId (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM claimcore.recovery_artifact_exports e "
                    + "WHERE e.operation_id=@operation AND e.case_id=@case "
                    + "AND e.expires_at>clock_timestamp()) AND "
                    + "(EXISTS (SELECT 1 FROM claimcore.cases c WHERE c.case_id=@case "
                    + "AND c.disposition='ACTIVE' AND c.privacy_phase='ACTIVE') OR "
                    + "EXISTS (SELECT 1 FROM claimcore.request_preparations p "
                    + "WHERE p.operation_id=@operation AND p.case_id=@case))",
                    connection,
                    transaction
                )

            Sql.uuid command "operation" operationId
            Sql.uuid command "case" caseId
            let! found = command.ExecuteScalarAsync(ct)
            return found :?> bool
        }

    let private suppressed connection transaction commitments reference operationId ct =
        task {
            let! referenceFence =
                CaseErasureSuppression.referenceBlocked
                    connection
                    transaction
                    commitments
                    reference
                    ct

            let! operationFence =
                CaseErasureSuppression.operationBlocked
                    connection
                    transaction
                    commitments
                    operationId
                    ct

            return referenceFence || operationFence
        }

    let admit
        dataSource
        (commitments: ISuppressionCommitments)
        principal
        caseId
        operationId
        reference
        (ct: CancellationToken)
        =
        task {
            if caseId = Guid.Empty || operationId = Guid.Empty then
                return None
            else
                let! connection, transaction, revision = openSnapshot dataSource ct
                use _connection = connection
                use _transaction = transaction

                let! blocked =
                    suppressed connection transaction commitments reference operationId ct

                let! current = caseIdByReferenceAny connection transaction reference ct
                let! available = availableCase connection transaction caseId ct
                let! found = exactExport connection transaction caseId operationId ct

                if
                    blocked
                    || not available
                    || (current.IsSome && current <> Some caseId)
                    || not found
                then
                    return None
                else
                    let! actor =
                        authorize
                            connection
                            transaction
                            revision
                            principal
                            EndpointAction.RecoveryImportRetain
                            (Some(ResourceScope.Case caseId))
                            ct

                    return
                        context commitments actor (Some caseId) EndpointAction.RecoveryImportRetain
        }
