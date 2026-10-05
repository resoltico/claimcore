namespace ClaimCore.Postgres

open System
open System.Data
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Live import proof reads only exact encrypted export evidence. It never repairs a missing
/// primary payload or invents a settlement from a witness intent.
module internal RecoveryArtifactImportProof =
    let private activeCaseOrPendingOpen
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (artifact: RecoveryArtifactV3)
        (request: CommandRequest)
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT case_reference,disposition,privacy_phase FROM claimcore.cases "
                    + "WHERE case_id=@case",
                    connection,
                    transaction
                )

            Sql.uuid command "case" artifact.CaseId
            use! reader = command.ExecuteReaderAsync(ct)
            let! exists = reader.ReadAsync(ct)

            if exists then
                return
                    reader.GetString(0) = request.CaseReference
                    && reader.GetString(1) = "ACTIVE"
                    && reader.GetString(2) = "ACTIVE"
            else
                do! reader.CloseAsync()

                match request.Command with
                | Command.Open _ when request.ExpectedVersion = 0L ->
                    use pending =
                        new NpgsqlCommand(
                            "SELECT EXISTS (SELECT 1 FROM claimcore.request_preparations "
                            + "WHERE operation_id=@operation AND case_id=@case)",
                            connection,
                            transaction
                        )

                    Sql.uuid pending "operation" artifact.OperationId
                    Sql.uuid pending "case" artifact.CaseId
                    let! found = pending.ExecuteScalarAsync(ct)
                    return found :?> bool
                | _ -> return false
        }

    let private allowed
        connection
        transaction
        revision
        (context: ActorCallContext)
        (artifact: RecoveryArtifactV3)
        request
        ct
        =
        task {
            let! referenceBlocked =
                CaseErasureSuppression.referenceBlocked
                    connection
                    transaction
                    context.Suppression
                    request.CaseReference
                    ct

            let! operationBlocked =
                CaseErasureSuppression.operationBlocked
                    connection
                    transaction
                    context.Suppression
                    artifact.OperationId
                    ct

            let! caseBlocked =
                CaseErasureSuppression.caseBlocked connection transaction artifact.CaseId ct

            let! active = activeCaseOrPendingOpen connection transaction artifact request ct

            let scope =
                match context.Action with
                | EndpointAction.RecoveryImportPreview -> Some ResourceScope.Installation
                | EndpointAction.RecoveryImportRetain when context.CaseId = Some artifact.CaseId ->
                    Some(ResourceScope.Case artifact.CaseId)
                | _ -> None

            match scope with
            | None -> return false
            | Some resource ->
                let! authorized =
                    ActorMutationGuard.authorizeScope
                        connection
                        transaction
                        context
                        resource
                        revision

                return
                    authorized
                    && active
                    && not referenceBlocked
                    && not operationBlocked
                    && not caseBlocked
        }

    let verify
        (source: NpgsqlDataSource)
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (artifact: RecoveryArtifactV3)
        (bytes: byte array)
        (ct: CancellationToken)
        =
        task {
            match RequestRecord.decode 65536 artifact.CanonicalRequest with
            | Error _ -> return false
            | Ok request when request.OperationId <> artifact.OperationId -> return false
            | Ok request ->
                use! connection = RuntimeDatabase.openConnectionAsyncWithCancellation source ct

                use! transaction =
                    connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)

                let! revision = ActorGrantRead.lockRevision connection transaction false ct

                let! authorized =
                    allowed connection transaction revision context artifact request ct

                if not authorized then
                    return false
                else
                    match!
                        RecoveryArtifactExportRead.find
                            connection
                            (Some transaction)
                            artifact.ExportId
                            ct
                    with
                    | None -> return false
                    | Some row ->
                        let! managed =
                            RecoveryArtifactExportRead.managedCopyMatches
                                connection
                                transaction
                                row
                                ct

                        if
                            not managed || not (RecoveryArtifactExportRead.exact row artifact bytes)
                        then
                            return false
                        else
                            try
                                do! RecoveryArtifactExportRead.requireSettled witness row ct
                                return true
                            with _ ->
                                return false
        }
