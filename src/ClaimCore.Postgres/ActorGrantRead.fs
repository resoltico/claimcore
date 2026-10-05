namespace ClaimCore.Postgres

open System
open System.Data
open System.IO
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application

/// The primary database is the live authority projection. A restored copy cannot reopen
/// admission until its authority events have been reconciled with the independent witness.
module internal ActorGrantRead =
    let private resourceCaseId =
        function
        | ResourceScope.Installation -> Guid.Empty
        | ResourceScope.Case caseId
        | ResourceScope.Operation(_, caseId) -> caseId

    /// A writer obtains FOR UPDATE; ordinary reads obtain FOR SHARE. Both conflict with
    /// grant-revision advancement, so an authorization snapshot cannot cross a revocation.
    let lockRevision
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        forMutation
        (cancellationToken: CancellationToken)
        =
        task {
            let suffix = if forMutation then " FOR UPDATE" else " FOR SHARE"

            use command =
                new NpgsqlCommand(
                    "SELECT revision FROM claimcore.authority_tip WHERE singleton" + suffix,
                    connection,
                    transaction
                )

            let! value = command.ExecuteScalarAsync(cancellationToken)

            match value with
            | :? int64 as revision when revision >= 0L ->
                if forMutation then
                    // The same primary authority lock is held through witness intent and
                    // primary COMMIT; owner copy transitions cannot overtake this check.
                    do!
                        CaseMutationCommitHealth.verifyLocked
                            connection
                            transaction
                            cancellationToken

                return revision
            | _ -> return raise (InvalidDataException("Authority revision is absent."))
        }

    let private findActor
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        principal
        (cancellationToken: CancellationToken)
        =
        task {
            let kind, issuer, stableValue = PrincipalKey.storageParts principal

            use actor =
                new NpgsqlCommand(
                    "SELECT actor_id, enabled FROM claimcore.actors "
                    + "WHERE principal_kind = @kind AND issuer = @issuer AND principal_value = @value",
                    connection,
                    transaction
                )

            Sql.text actor "kind" kind
            Sql.text actor "issuer" issuer
            Sql.text actor "value" stableValue
            let! result = actor.ExecuteReaderAsync(cancellationToken)
            use reader = result
            let! found = reader.ReadAsync(cancellationToken)

            return
                if found then
                    Some(reader.GetGuid(0), reader.GetBoolean(1))
                else
                    None
        }

    let private readGrants
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        actorId
        resource
        (cancellationToken: CancellationToken)
        =
        task {
            use grants =
                new NpgsqlCommand(
                    "SELECT scope_kind, scope_case_id, role_name FROM claimcore.actor_grants "
                    + "WHERE actor_id = @actor AND active AND "
                    + "(scope_kind = 'INSTALLATION' OR (scope_kind = 'CASE' AND scope_case_id = @caseId)) "
                    + "ORDER BY scope_kind, scope_case_id, role_name",
                    connection,
                    transaction
                )

            Sql.uuid grants "actor" actorId
            Sql.uuid grants "caseId" (resourceCaseId resource)
            let! rows = grants.ExecuteReaderAsync(cancellationToken)
            use reader = rows
            let collected = ResizeArray<ActorGrant>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync(cancellationToken)
                reading <- hasRow

                if hasRow then
                    let grant =
                        ActorGrantCandidate.grantFromStorage
                            (reader.GetString(0))
                            (reader.GetGuid(1))
                            (reader.GetString(2))
                        |> Option.defaultWith (fun () ->
                            raise (InvalidDataException("Stored grant scope is invalid.")))

                    collected.Add(grant)

            return List.ofSeq collected
        }

    let loadUnderLock connection transaction principal resource revision cancellationToken =
        task {
            let! actor = findActor connection transaction principal cancellationToken

            match actor with
            | None -> return None
            | Some(actorId, enabled) ->
                let! grants = readGrants connection transaction actorId resource cancellationToken

                return
                    Some
                        {
                            ActorId = actorId
                            Principal = principal
                            Enabled = enabled
                            GrantRevision = revision
                            Grants = grants
                        }
        }

type internal ActorGrantStore(dataSource: NpgsqlDataSource) =
    member private _.AuthorizeResolved
        (
            principal: PrincipalKey,
            action: EndpointAction,
            resource: ResourceScope option,
            connection: NpgsqlConnection,
            transaction: NpgsqlTransaction,
            revision: int64,
            cancellationToken: CancellationToken
        ) =
        task {
            // Run the same actor/grant lookup even when the resource is absent. A missing
            // identity and an inaccessible one then share the public result and query class.
            let target = resource |> Option.defaultValue (ResourceScope.Case Guid.Empty)

            let! authority =
                ActorGrantRead.loadUnderLock
                    connection
                    transaction
                    principal
                    target
                    revision
                    cancellationToken

            return
                match resource, authority with
                | Some actual, Some value ->
                    ActorAuthorization.authorize principal value action actual
                | _ -> AuthorizationDecision.Unavailable
        }

    /// Both a nonexistent and an inaccessible case return the same unavailable tag.
    member this.AuthorizeCaseReference(principal, action, reference, cancellationToken) =
        task {
            use! connection =
                RuntimeDatabase.openConnectionAsyncWithCancellation dataSource cancellationToken

            use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

            let! revision =
                ActorGrantRead.lockRevision connection transaction false cancellationToken

            use command =
                new NpgsqlCommand(
                    "SELECT case_id FROM claimcore.cases WHERE case_reference=@reference",
                    connection,
                    transaction
                )

            Sql.text command "reference" reference
            let! value = command.ExecuteScalarAsync(cancellationToken)

            let resource =
                match value with
                | :? Guid as caseId when caseId <> Guid.Empty -> Some(ResourceScope.Case caseId)
                | _ -> None

            return!
                this.AuthorizeResolved(
                    principal,
                    action,
                    resource,
                    connection,
                    transaction,
                    revision,
                    cancellationToken
                )
        }

    /// Accepted operation identity is resolved in storage, not trusted from a caller-supplied
    /// case ID. Pending preparations require a separate operation-bound recovery authority path.
    member this.AuthorizeAcceptedOperation(principal, action, operationId, cancellationToken) =
        task {
            use! connection =
                RuntimeDatabase.openConnectionAsyncWithCancellation dataSource cancellationToken

            use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

            let! revision =
                ActorGrantRead.lockRevision connection transaction false cancellationToken

            use command =
                new NpgsqlCommand(
                    "SELECT case_id FROM claimcore.case_changes WHERE operation_id=@operation",
                    connection,
                    transaction
                )

            Sql.uuid command "operation" operationId
            let! value = command.ExecuteScalarAsync(cancellationToken)

            let resource =
                match value with
                | :? Guid as caseId when caseId <> Guid.Empty ->
                    Some(ResourceScope.Operation(operationId, caseId))
                | _ -> None

            return!
                this.AuthorizeResolved(
                    principal,
                    action,
                    resource,
                    connection,
                    transaction,
                    revision,
                    cancellationToken
                )
        }

    member _.LockAndLoad(connection, transaction, principal, resource, cancellationToken) =
        task {
            let! revision =
                ActorGrantRead.lockRevision connection transaction true cancellationToken

            return!
                ActorGrantRead.loadUnderLock
                    connection
                    transaction
                    principal
                    resource
                    revision
                    cancellationToken
        }

    interface IActorGrantSource with
        member _.LoadForScope(principal, resource, cancellationToken) =
            task {
                use! connection = RuntimeDatabase.openConnectionAsync dataSource

                let! transaction =
                    connection.BeginTransactionAsync(
                        IsolationLevel.ReadCommitted,
                        cancellationToken
                    )

                use _ = transaction

                let! revision =
                    ActorGrantRead.lockRevision connection transaction false cancellationToken

                let! authority =
                    ActorGrantRead.loadUnderLock
                        connection
                        transaction
                        principal
                        resource
                        revision
                        cancellationToken

                return authority
            }
