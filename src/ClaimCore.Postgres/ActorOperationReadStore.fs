namespace ClaimCore.Postgres

open System
open System.Threading
open ClaimCore.Application

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

    let operation dataSource (context: ActorCallContext) operationId =
        ActorReadStore.snapshot dataSource (fun connection transaction revision ->
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

                        return Ok(receipt |> Option.map fst)
            })

    let accepted dataSource (context: ActorCallContext) operationId digest =
        ActorReadStore.snapshot dataSource (fun connection transaction revision ->
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
                        return!
                            StoreData.readAcceptedUnderLock
                                connection
                                transaction
                                operationId
                                digest
            })
