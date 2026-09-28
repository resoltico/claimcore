namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain

/// Called only after authority_tip and the operation/case locks, immediately before a
/// candidate intent. This rechecks the same principal, case ID and grant revision observed
/// at actor-bound acquisition; a previous preview or preparation never grants commit power.
module internal ActorMutationGuard =
    let authorizeScope connection transaction (context: ActorCallContext) resource revision =
        task {
            let! suppressed =
                ActorMutationSuppression.scopeBlocked connection transaction context resource

            if suppressed then
                return false
            else
                let! authority =
                    ActorGrantRead.loadUnderLock
                        connection
                        transaction
                        context.Binding.Principal
                        resource
                        revision
                        CancellationToken.None

                return
                    match authority with
                    | None -> false
                    | Some value ->
                        match
                            ActorAuthorization.authorizeAtRevision
                                context.Binding.Principal
                                value
                                context.Binding.GrantRevision
                                context.Action
                                resource
                        with
                        | AuthorizationDecision.Available(actorId, _) ->
                            actorId = context.Binding.ActorId
                        | AuthorizationDecision.Unavailable -> false
        }

    let authorize
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (context: ActorCallContext)
        (request: CommandRequest)
        (caseId: Guid)
        revision
        =
        task {
            let! blocked =
                ActorGrantGateQueries.blockedReference
                    connection
                    transaction
                    request.CaseReference
                    CancellationToken.None

            let! erased =
                ActorMutationSuppression.commandBlocked connection transaction context request

            let! available =
                ActorGrantGateQueries.availableCase
                    connection
                    transaction
                    caseId
                    CancellationToken.None

            let tentativeOpen =
                match request.Command, context.Action with
                | Command.Open _, (EndpointAction.PrepareNewCase | EndpointAction.ExecuteNewCase) ->
                    true
                | _ -> false

            if
                blocked
                || erased
                || not available
                || (context.CaseId <> Some caseId && not tentativeOpen)
            then
                return false
            else
                let resource =
                    match context.Action, request.Command with
                    | EndpointAction.RecoveryResolve, _ ->
                        ResourceScope.Operation(request.OperationId, caseId)
                    | EndpointAction.RecoveryImportRetain, _ -> ResourceScope.Case caseId
                    | _, Command.Open _ -> ResourceScope.Installation
                    | _ -> ResourceScope.Case caseId

                return! authorizeScope connection transaction context resource revision
        }
