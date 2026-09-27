namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application

/// Resolve the stored case before recovery disclosure and recheck actor authority in the read transaction.
module internal RecoveryReadAuthority =
    let storedScope connection transaction operationId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT p.case_id,p.preparer_actor_id,c.case_id,r.case_id "
                    + "FROM (SELECT @operation::uuid AS id) target "
                    + "LEFT JOIN claimcore.request_preparations p ON p.operation_id=target.id "
                    + "LEFT JOIN claimcore.case_changes c ON c.operation_id=target.id "
                    + "LEFT JOIN claimcore.operation_revocations r ON r.operation_id=target.id",
                    connection,
                    transaction
                )

            Sql.uuid command "operation" operationId
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! found = reader.ReadAsync()

            if not found then
                return None
            else
                let optional index =
                    if reader.IsDBNull(index) then
                        None
                    else
                        Some(reader.GetGuid(index))

                return Some(optional 0, optional 1, optional 2, optional 3)
        }

    let private normalScope action caseId =
        match action, caseId with
        | EndpointAction.PrepareNewCase, _
        | EndpointAction.ExecuteNewCase, _ -> Some ResourceScope.Installation
        | EndpointAction.PrepareCommand, Some id
        | EndpointAction.ExecuteCommand, Some id -> Some(ResourceScope.Case id)
        | _ -> None

    let private authorizeStored
        connection
        transaction
        revision
        operationId
        (context: ActorCallContext)
        recoveryAction
        preparedCase
        preparer
        acceptedCase
        revokedCase
        =
        task {
            let caseId = preparedCase |> Option.orElse acceptedCase |> Option.orElse revokedCase

            match caseId with
            | None -> return false
            | Some id ->
                let! available =
                    ActorGrantGateQueries.availableCase
                        connection
                        transaction
                        id
                        CancellationToken.None

                let resource =
                    if recoveryAction then
                        Some(ResourceScope.Operation(operationId, id))
                    else
                        normalScope context.Action caseId

                match resource with
                | None -> return false
                | Some target ->
                    let original = recoveryAction || preparer = Some context.Binding.ActorId

                    let sameCase =
                        context.Action = EndpointAction.PrepareNewCase
                        || context.Action = EndpointAction.ExecuteNewCase
                        || context.CaseId = Some id

                    if not available || not original || not sameCase then
                        return false
                    else
                        return!
                            ActorMutationGuard.authorizeScope
                                connection
                                transaction
                                context
                                target
                                revision
        }

    let mayRead connection transaction revision operationId (context: ActorCallContext) scope =
        task {
            let recoveryAction =
                match context.Action with
                | EndpointAction.RecoveryInspect
                | EndpointAction.RecoveryResolve
                | EndpointAction.RecoveryDismiss
                | EndpointAction.RecoveryExport -> true
                | _ -> false

            match scope with
            | None -> return not recoveryAction
            | Some(None, None, None, None) -> return not recoveryAction
            | Some(preparedCase, preparer, acceptedCase, revokedCase) ->
                return!
                    authorizeStored
                        connection
                        transaction
                        revision
                        operationId
                        context
                        recoveryAction
                        preparedCase
                        preparer
                        acceptedCase
                        revokedCase
        }
