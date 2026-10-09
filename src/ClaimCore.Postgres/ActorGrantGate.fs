namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ActorGrantGateQueries
open ActorGrantGateBinding

/// Resolves opaque resources and grants inside one authority-revision snapshot. No caller
/// supplies a case ID for case or operation disclosure.
type internal PostgresActorGate(dataSource: NpgsqlDataSource, commitments: ISuppressionCommitments)
    =
    let openSnapshot cancellationToken =
        ActorGrantGateQueries.openSnapshot dataSource cancellationToken

    interface IActorGate with
        member _.Installation(principal, action, cancellationToken) =
            task {
                if
                    action <> EndpointAction.ApproveCopySigner
                    && action <> EndpointAction.ApproveCopyDeletion
                    && action <> EndpointAction.ApproveWriterHandoff
                    && action <> EndpointAction.ReviewRealDataActivation
                    && action <> EndpointAction.ApproveRealDataActivation
                    && action <> EndpointAction.RecoveryImportPreview
                    && action <> EndpointAction.RecoveryList
                then
                    return None
                else
                    let! connection, transaction, revision = openSnapshot cancellationToken
                    use _connection = connection
                    use _transaction = transaction

                    let! actor =
                        authorize
                            connection
                            transaction
                            revision
                            principal
                            action
                            (Some ResourceScope.Installation)
                            cancellationToken

                    return context commitments actor None action
            }

        member _.Definition(principal, cancellationToken) =
            task {
                let! connection, transaction, revision = openSnapshot cancellationToken
                use _connection = connection
                use _transaction = transaction

                let! actor =
                    authorize
                        connection
                        transaction
                        revision
                        principal
                        EndpointAction.Definition
                        (Some ResourceScope.Installation)
                        cancellationToken

                return context commitments actor None EndpointAction.Definition
            }

        member _.Command(principal, action, request, cancellationToken) =
            task {
                if not (validCommandAction action request) then
                    return None
                else
                    let! connection, transaction, revision = openSnapshot cancellationToken
                    use _connection = connection
                    use _transaction = transaction

                    let! referenceBlocked =
                        CaseErasureSuppression.referenceBlocked
                            connection
                            transaction
                            commitments
                            request.CaseReference
                            cancellationToken

                    let! operationBlocked =
                        CaseErasureSuppression.operationBlocked
                            connection
                            transaction
                            commitments
                            request.OperationId
                            cancellationToken

                    match referenceBlocked || operationBlocked, request.Command with
                    | true, _ -> return None
                    | false, Command.Open _ ->
                        return!
                            bindOpen
                                commitments
                                connection
                                transaction
                                revision
                                principal
                                action
                                request
                                cancellationToken
                    | false, _ ->
                        return!
                            bindExisting
                                commitments
                                connection
                                transaction
                                revision
                                principal
                                action
                                request
                                cancellationToken
            }

        member _.Case(principal, action, reference, cancellationToken) =
            task {
                let! connection, transaction, revision = openSnapshot cancellationToken
                use _connection = connection
                use _transaction = transaction

                let lifecycle =
                    match action with
                    | EndpointAction.ManageHolds
                    | EndpointAction.ReviewLifecycle
                    | EndpointAction.VoidCase
                    | EndpointAction.ReinstateCase
                    | EndpointAction.RequestErasure
                    | EndpointAction.ApproveLifecycle
                    | EndpointAction.ApproveErasure -> true
                    | _ -> false

                let! suppressed =
                    if lifecycle then
                        System.Threading.Tasks.Task.FromResult false
                    else
                        CaseErasureSuppression.referenceBlocked
                            connection
                            transaction
                            commitments
                            reference
                            cancellationToken

                let! located =
                    if lifecycle then
                        caseIdByReferenceAny connection transaction reference cancellationToken
                    else
                        caseIdByReference connection transaction reference cancellationToken

                // Even a suppressed reference takes the same indexed lookup and authority query
                // class as an absent one. Its resolved identity is never disclosed or admitted.
                let caseId = if suppressed then None else located

                let! actor =
                    authorize
                        connection
                        transaction
                        revision
                        principal
                        action
                        (caseId |> Option.map ResourceScope.Case)
                        cancellationToken

                return context commitments actor caseId action
            }

        member _.Tombstone(principal, action, caseId, cancellationToken) =
            ActorGrantTombstoneGate.admit
                dataSource
                commitments
                principal
                action
                caseId
                cancellationToken

        member _.Import(principal, caseId, operationId, reference, cancellationToken) =
            ActorGrantImportGate.admit
                dataSource
                commitments
                principal
                caseId
                operationId
                reference
                cancellationToken

        member _.Operation(principal, action, operationId, cancellationToken) =
            task {
                let! connection, transaction, revision = openSnapshot cancellationToken
                use _connection = connection
                use _transaction = transaction

                let! blocked =
                    CaseErasureSuppression.operationBlocked
                        connection
                        transaction
                        commitments
                        operationId
                        cancellationToken

                let! located =
                    match action with
                    | EndpointAction.ObserveOperation ->
                        acceptedCaseId connection transaction operationId cancellationToken
                    | EndpointAction.RecoveryInspect
                    | EndpointAction.RecoveryResolve
                    | EndpointAction.RecoveryDismiss
                    | EndpointAction.RecoveryExport
                    | EndpointAction.RecoveryImportRetain ->
                        operationCaseId connection transaction operationId cancellationToken
                    | _ -> task { return None }

                // A suppression fence changes the authorization result, not the public query
                // class or the number of resource lookups visible to a caller.
                let caseId = if blocked then None else located

                let resource =
                    caseId
                    |> Option.map (fun id ->
                        if action = EndpointAction.RecoveryImportRetain then
                            ResourceScope.Case id
                        else
                            ResourceScope.Operation(operationId, id))

                let! actor =
                    authorize
                        connection
                        transaction
                        revision
                        principal
                        action
                        resource
                        cancellationToken

                return context commitments actor caseId action
            }

        member _.List(principal, cancellationToken) =
            task {
                let! connection, transaction, revision = openSnapshot cancellationToken
                use _connection = connection
                use _transaction = transaction

                let! authority =
                    ActorGrantRead.loadUnderLock
                        connection
                        transaction
                        principal
                        ResourceScope.Installation
                        revision
                        cancellationToken

                let actor =
                    authority
                    |> Option.filter _.Enabled
                    |> Option.map (fun value ->
                        ({
                            Principal = principal
                            ActorId = value.ActorId
                            GrantRevision = value.GrantRevision
                         },
                         ActorActionAdvice.mayEditCommands
                             principal
                             value
                             ResourceScope.Installation,
                         []))

                return context commitments actor None EndpointAction.ListCases
            }
