namespace ClaimCore.Postgres

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open OperationAuthorityStore
open PreparationData

/// Read-only recovery views combine the retained header with current accepted/revoked authority.
module internal RecoveryReadStore =
    let private pendingStored (preparation: RetainedPreparation option) =
        match preparation with
        | Some header ->
            match header.Lifecycle with
            | PreparationLifecycle.Dismissed _ ->
                raise (InvalidDataException("Dismissed preparation lacks revocation authority."))
            | PreparationLifecycle.Unsubmitted
            | PreparationLifecycle.SubmissionStarted _ ->
                Some(RecoveryStoredOperation.Retained(header, RecoveryAuthority.PendingAuthority))
        | None -> None

    let private classifyStored (preparation: RetainedPreparation option) accepted revoked =
        match preparation, accepted, revoked with
        | _, Some _, Some _ ->
            raise (InvalidDataException("Accepted and revoked authority coexist."))
        | Some header, Some(_, fingerprint), None when header.RequestSha256 = fingerprint ->
            Some(RecoveryStoredOperation.Retained(header, RecoveryAuthority.AcceptedAuthority))
        | Some _, Some _, None ->
            raise (InvalidDataException("Accepted operation identity disagrees with recovery."))
        | None, Some _, None -> None
        | Some header, None, Some revocation when matches header.RequestSha256 revocation ->
            Some(RecoveryStoredOperation.Retained(header, RecoveryAuthority.RevokedAuthority))
        | Some _, None, Some _ ->
            raise (InvalidDataException("Revoked operation identity disagrees with recovery."))
        | None, None, Some revocation ->
            Some(RecoveryStoredOperation.RevokedTombstone(project revocation))
        | preparation, None, None -> pendingStored preparation

    let private stored
        (connection: NpgsqlConnection)
        (operationId: Guid)
        : Task<RecoveryStoredOperation option> =
        task {
            let! preparation = readHeader connection None operationId
            let! accepted = StoreData.readOperation connection None operationId
            let! revoked = findRead connection operationId
            return classifyStored preparation accepted revoked
        }

    let get
        (dataSource: NpgsqlDataSource)
        (operationId: Guid)
        (cancellationToken: CancellationToken)
        : Task<Result<RecoveryStoredOperation option, RecoveryStoreFailure>> =
        task {
            if operationId = Guid.Empty then
                return Error(RecoveryStoreFailure.InvalidInput "operationId")
            else
                try
                    cancellationToken.ThrowIfCancellationRequested()
                    use! connection = RuntimeDatabase.openConnectionAsync dataSource
                    let! result = stored connection operationId
                    cancellationToken.ThrowIfCancellationRequested()
                    return Ok result
                with
                | :? OperationCanceledException -> return Error RecoveryStoreFailure.ReadCancelled
                | error -> return Error(fail error)
        }

    let inspect
        (dataSource: NpgsqlDataSource)
        (limits: PreparationLimits)
        (operationId: Guid)
        (after: RecoveryAttemptCursor option)
        pageSize
        (cancellationToken: CancellationToken)
        : Task<Result<RecoveryStoreInspection option, RecoveryStoreFailure>> =
        task {
            if
                operationId = Guid.Empty
                || pageSize < 1
                || pageSize > limits.MaximumPageSize
                || after |> Option.exists (fun cursor -> cursor.OperationId <> operationId)
            then
                return Error(RecoveryStoreFailure.InvalidInput "recovery inspection")
            else
                try
                    cancellationToken.ThrowIfCancellationRequested()
                    use! connection = RuntimeDatabase.openConnectionAsync dataSource
                    let! result = stored connection operationId

                    match result with
                    | None -> return Ok None
                    | Some(RecoveryStoredOperation.RevokedTombstone revocation) ->
                        return Ok(Some(RecoveryStoreInspection.RevokedTombstone revocation))
                    | Some(RecoveryStoredOperation.Retained(header, authority)) ->
                        let! evidence =
                            PreparationEvidence.readPage connection None operationId after pageSize

                        cancellationToken.ThrowIfCancellationRequested()

                        return
                            Ok(Some(RecoveryStoreInspection.Retained(header, evidence, authority)))
                with
                | :? OperationCanceledException -> return Error RecoveryStoreFailure.ReadCancelled
                | error -> return Error(fail error)
        }

    let list
        (dataSource: NpgsqlDataSource)
        (limits: PreparationLimits)
        view
        (after: RecoveryCursor option)
        pageSize
        (cancellationToken: CancellationToken)
        : Task<Result<RecoveryStorePage, RecoveryStoreFailure>> =
        task {
            if
                pageSize < 1
                || pageSize > limits.MaximumPageSize
                || after |> Option.exists (fun cursor -> cursor.View <> view)
            then
                return Error(RecoveryStoreFailure.InvalidInput "recovery list")
            else
                try
                    cancellationToken.ThrowIfCancellationRequested()
                    use! connection = RuntimeDatabase.openConnectionAsync dataSource
                    let! page = PreparationPageStore.list connection limits view after pageSize
                    cancellationToken.ThrowIfCancellationRequested()
                    return Ok page
                with
                | :? OperationCanceledException -> return Error RecoveryStoreFailure.ReadCancelled
                | error -> return Error(fail error)
        }
