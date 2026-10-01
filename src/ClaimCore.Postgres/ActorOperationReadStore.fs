namespace ClaimCore.Postgres

open System
open System.Threading
open ClaimCore.Application
open ClaimCore.Postgres.WitnessProtocolReconciliation

/// Operation observations are admitted by exact operation/case identity, not a caller-supplied case.
module internal ActorOperationReadStore =
    let private recoveryAction =
        function
        | EndpointAction.RecoveryInspect
        | EndpointAction.RecoveryResolve
        | EndpointAction.RecoveryDismiss
        | EndpointAction.RecoveryExport -> true
        | _ -> false

    let private freshCommand =
        function
        | EndpointAction.PrepareNewCase
        | EndpointAction.ExecuteNewCase
        | EndpointAction.PrepareCommand
        | EndpointAction.ExecuteCommand -> true
        | _ -> false

    let private tentativeOpen =
        function
        | EndpointAction.PrepareNewCase
        | EndpointAction.ExecuteNewCase -> true
        | _ -> false

    let private operationCaseId connection transaction (context: ActorCallContext) operationId =
        if recoveryAction context.Action then
            ActorGrantGateQueries.operationCaseId
                connection
                transaction
                operationId
                CancellationToken.None
        else
            ActorGrantGateQueries.acceptedCaseId
                connection
                transaction
                operationId
                CancellationToken.None

    let private mutatesEvidence (context: ActorCallContext) =
        freshCommand context.Action
        || context.Action = EndpointAction.RecoveryResolve
        || context.Action = EndpointAction.RecoveryDismiss

    let private snapshot dataSource context action =
        if not (mutatesEvidence context) then
            ActorReadStore.snapshot dataSource action
        else
            task {
                let! result =
                    StoreData.read dataSource (fun connection ->
                        task {
                            use! _lease =
                                AuthorityOperationFence.acquireShared
                                    (Some dataSource)
                                    connection
                                    CancellationToken.None

                            use transaction =
                                connection.BeginTransaction(
                                    System.Data.IsolationLevel.ReadCommitted
                                )

                            let! revision =
                                ActorGrantRead.lockRevision
                                    connection
                                    transaction
                                    true
                                    CancellationToken.None

                            return! action connection transaction revision
                        })

                return result |> Result.bind id
            }

    let private witnessed
        (witness: WitnessProtocol)
        context
        connection
        transaction
        (operationId: Guid)
        receipt
        =
        try
            if mutatesEvidence context then
                Sql.lockKey connection transaction ("operation:" + operationId.ToString("D"))
                witness.ReconcileAccepted(connection, transaction, operationId)
            else
                witness.VerifyAccepted(connection, transaction, operationId)

            Ok receipt
        with _ ->
            Error(CoreFailure.CommitOutcomeUnknown operationId)

    let operation dataSource witness (context: ActorCallContext) operationId =
        snapshot dataSource context (fun connection transaction revision ->
            task {
                let! found = operationCaseId connection transaction context operationId

                match found with
                | None when freshCommand context.Action -> return Ok None
                | None -> return Error CoreFailure.ResourceUnavailable
                | Some id when context.CaseId <> Some id && not (tentativeOpen context.Action) ->
                    return Error CoreFailure.ResourceUnavailable
                | Some id ->
                    let action =
                        if recoveryAction context.Action then
                            context.Action
                        else
                            EndpointAction.ObserveOperation

                    let! canRead =
                        ActorReadStore.allowed
                            connection
                            transaction
                            revision
                            context
                            (ResourceScope.Operation(operationId, id))
                            action

                    if not canRead then
                        return Error CoreFailure.ResourceUnavailable
                    else
                        let! receipt =
                            StoreData.readOperation connection (Some transaction) operationId

                        match receipt with
                        | None -> return Ok None
                        | Some(value, _) ->
                            return
                                witnessed witness context connection transaction operationId value
                                |> Result.map Some
            })

    let accepted dataSource witness (context: ActorCallContext) operationId digest =
        snapshot dataSource context (fun connection transaction revision ->
            task {
                let! found =
                    ActorGrantGateQueries.acceptedCaseId
                        connection
                        transaction
                        operationId
                        CancellationToken.None

                match found with
                | None -> return Ok None
                | Some id when context.CaseId <> Some id && not (tentativeOpen context.Action) ->
                    return Error CoreFailure.ResourceUnavailable
                | Some id ->
                    let recovery = recoveryAction context.Action

                    let scope =
                        if recovery then
                            ResourceScope.Operation(operationId, id)
                        else
                            ResourceScope.Case id

                    let action = if recovery then context.Action else EndpointAction.GetCase

                    let! canRead =
                        ActorReadStore.allowed connection transaction revision context scope action

                    if not canRead then
                        return Error CoreFailure.ResourceUnavailable
                    else
                        let! observed =
                            StoreData.readAcceptedUnderLock
                                connection
                                transaction
                                operationId
                                digest

                        match observed with
                        | Ok(Some receipt) ->
                            return
                                witnessed
                                    witness
                                    context
                                    connection
                                    transaction
                                    operationId
                                    receipt
                                |> Result.map Some
                        | other -> return other
            })
