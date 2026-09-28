namespace ClaimCore.Postgres

open System.Threading
open ClaimCore.Application
open ClaimCore.Domain

module internal ActorMutationSuppression =
    let private lifecycleCasework =
        function
        | EndpointAction.ManageHolds
        | EndpointAction.ReviewLifecycle
        | EndpointAction.VoidCase
        | EndpointAction.ReinstateCase
        | EndpointAction.RequestErasure
        | EndpointAction.ApproveLifecycle
        | EndpointAction.ApproveErasure
        | EndpointAction.ApproveCopyAdoption -> true
        | _ -> false

    let private lifecyclePrivacy =
        function
        | EndpointAction.ReviewTombstone
        | EndpointAction.ManageTombstoneHold
        | EndpointAction.ApproveWitnessPrune -> true
        | EndpointAction.ApproveTerminalErasure -> true
        | _ -> false

    let private lifecycle action =
        lifecycleCasework action || lifecyclePrivacy action

    let scopeBlocked connection transaction (context: ActorCallContext) resource =
        match resource with
        | ResourceScope.Installation -> task { return false }
        | ResourceScope.Case _ when lifecycle context.Action -> task { return false }
        | ResourceScope.Case caseId ->
            CaseErasureSuppression.caseBlocked connection transaction caseId CancellationToken.None
        | ResourceScope.Operation(operationId, caseId) ->
            task {
                let! caseFence =
                    CaseErasureSuppression.caseBlocked
                        connection
                        transaction
                        caseId
                        CancellationToken.None

                let! operationFence =
                    CaseErasureSuppression.operationBlocked
                        connection
                        transaction
                        context.Suppression
                        operationId
                        CancellationToken.None

                return caseFence || operationFence
            }

    let commandBlocked
        connection
        transaction
        (context: ActorCallContext)
        (request: CommandRequest)
        =
        task {
            let! reference =
                CaseErasureSuppression.referenceBlocked
                    connection
                    transaction
                    context.Suppression
                    request.CaseReference
                    CancellationToken.None

            let! operation =
                CaseErasureSuppression.operationBlocked
                    connection
                    transaction
                    context.Suppression
                    request.OperationId
                    CancellationToken.None

            return reference || operation
        }
