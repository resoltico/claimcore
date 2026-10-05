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

    let private operationCaseId connection transaction (context: ActorCallContext) operationId ct =
        if recoveryAction context.Action then
            ActorGrantGateQueries.operationCaseId connection transaction operationId ct
        else
            ActorGrantGateQueries.acceptedCaseId connection transaction operationId ct

    let private mutatesEvidence (context: ActorCallContext) =
        freshCommand context.Action
        || context.Action = EndpointAction.RecoveryResolve
        || context.Action = EndpointAction.RecoveryDismiss

    let private snapshot dataSource context ct action =
        if not (mutatesEvidence context) then
            ActorReadStore.snapshot dataSource ct action
        else
            task {
                let! result =
                    StoreData.read dataSource ct (fun connection ->
                        task {
                            use! _lease =
                                AuthorityOperationFence.acquireShared
                                    (Some dataSource)
                                    connection
                                    ct

                            use! transaction =
                                connection.BeginTransactionAsync(
                                    System.Data.IsolationLevel.ReadCommitted,
                                    ct
                                )

                            let! revision =
                                ActorGrantRead.lockRevision connection transaction true ct

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
        (ct: CancellationToken)
        =
        task {
            try
                if mutatesEvidence context then
                    do!
                        Sql.lockKeyAsync
                            connection
                            transaction
                            ("operation:" + operationId.ToString("D"))
                            ct

                    do! witness.ReconcileAccepted(connection, transaction, operationId, ct)
                else
                    do! witness.VerifyAccepted(connection, transaction, operationId, ct)

                return Ok receipt
            with
            | :? OperationCanceledException as error when ct.IsCancellationRequested ->
                return raise error
            | _ -> return Error(CoreFailure.CommitOutcomeUnknown operationId)
        }

    let operation dataSource witness (context: ActorCallContext) operationId ct =
        snapshot dataSource context ct (fun connection transaction revision ->
            task {
                let! found = operationCaseId connection transaction context operationId ct

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
                            ct

                    if not canRead then
                        return Error CoreFailure.ResourceUnavailable
                    else
                        let! receipt =
                            StoreData.readOperation connection (Some transaction) operationId

                        match receipt with
                        | None -> return Ok None
                        | Some(value, _) ->
                            let! proof =
                                witnessed
                                    witness
                                    context
                                    connection
                                    transaction
                                    operationId
                                    value
                                    ct

                            return proof |> Result.map Some
            })

    let accepted dataSource witness (context: ActorCallContext) operationId digest ct =
        snapshot dataSource context ct (fun connection transaction revision ->
            task {
                let! found =
                    ActorGrantGateQueries.acceptedCaseId connection transaction operationId ct

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
                        ActorReadStore.allowed
                            connection
                            transaction
                            revision
                            context
                            scope
                            action
                            ct

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
                            let! proof =
                                witnessed
                                    witness
                                    context
                                    connection
                                    transaction
                                    operationId
                                    receipt
                                    ct

                            return proof |> Result.map Some
                        | other -> return other
            })
