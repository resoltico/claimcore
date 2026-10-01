namespace ClaimCore.Postgres

open System
open System.Data
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.Domain
open WitnessProtocolReconciliation

/// Read operations hold the authority revision through disclosure. A grant change cannot slip
/// between the final authorization check and a claimant-bearing row read.
module internal ActorReadStore =
    let snapshot (dataSource: NpgsqlDataSource) action =
        task {
            let! result =
                StoreData.read dataSource (fun connection ->
                    task {
                        use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

                        let! revision =
                            ActorGrantRead.lockRevision
                                connection
                                transaction
                                false
                                CancellationToken.None

                        return! action connection transaction revision
                    })

            return result |> Result.bind id
        }

    let private caseId connection transaction reference =
        ActorGrantGateQueries.caseIdByReference
            connection
            transaction
            reference
            CancellationToken.None

    let allowed connection transaction revision (context: ActorCallContext) resource action =
        task {
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
                | Some value ->
                    match
                        ActorAuthorization.authorizeAtRevision
                            context.Binding.Principal
                            value
                            context.Binding.GrantRevision
                            action
                            resource
                    with
                    | AuthorizationDecision.Available(actorId, _) ->
                        actorId = context.Binding.ActorId
                    | AuthorizationDecision.Unavailable -> false
                | None -> false
        }

    let get dataSource witness (context: ActorCallContext) reference =
        snapshot dataSource (fun connection transaction revision ->
            task {
                let! found = caseId connection transaction reference

                match found with
                | None when
                    context.Action = EndpointAction.PrepareNewCase
                    || context.Action = EndpointAction.ExecuteNewCase
                    ->
                    return Ok None
                | None -> return Error CoreFailure.ResourceUnavailable
                | Some id when context.CaseId <> Some id ->
                    return Error CoreFailure.ResourceUnavailable
                | Some id ->
                    let! canRead =
                        allowed
                            connection
                            transaction
                            revision
                            context
                            (ResourceScope.Case id)
                            EndpointAction.GetCase

                    if not canRead then
                        return Error CoreFailure.ResourceUnavailable
                    else
                        use command = new NpgsqlCommand(Sql.selectCase, connection, transaction)
                        Sql.text command "reference" reference
                        let! result = command.ExecuteReaderAsync()
                        use reader = result
                        let! exists = reader.ReadAsync()
                        let value = if exists then Some(Rows.claim reader) else None
                        reader.Close()

                        match value with
                        | None -> return Ok None
                        | Some claim ->
                            return
                                CaseReadEvidence.current witness connection transaction claim
                                |> Result.map (fun () -> Some claim)
            })

    let private verifyHistory witness connection transaction receipts =
        receipts
        |> List.tryPick (fun (receipt: Receipt) ->
            match CaseReadEvidence.receipt witness connection transaction receipt.OperationId with
            | Ok() -> None
            | Error failure -> Some failure)

    let private historyRows connection transaction reference afterVersion =
        task {
            use command = new NpgsqlCommand(Sql.history, connection, transaction)
            Sql.text command "reference" reference
            Sql.integer command "after" afterVersion
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let rows = ResizeArray<Receipt>()
            let mutable reading = true

            while reading do
                let! exists = reader.ReadAsync()
                reading <- exists

                if exists then
                    rows.Add(Rows.receipt reader false)

            reader.Close()
            return rows
        }

    let history dataSource witness (context: ActorCallContext) reference afterVersion =
        snapshot dataSource (fun connection transaction revision ->
            task {
                let! found = caseId connection transaction reference

                match found with
                | None -> return Error CoreFailure.ResourceUnavailable
                | Some id when context.CaseId <> Some id ->
                    return Error CoreFailure.ResourceUnavailable
                | Some id ->
                    let! canRead =
                        allowed
                            connection
                            transaction
                            revision
                            context
                            (ResourceScope.Case id)
                            context.Action

                    if not canRead then
                        return Error CoreFailure.ResourceUnavailable
                    else
                        let! rows = historyRows connection transaction reference afterVersion
                        let limit = SemanticContract.current.MaximumPageSize
                        let items = rows |> Seq.truncate limit |> Seq.toList

                        let next =
                            if rows.Count > limit then
                                items
                                |> List.tryLast
                                |> Option.map (fun value -> (Claim.view value.Case).Version)
                            else
                                None

                        match verifyHistory witness connection transaction items with
                        | Some failure -> return Error failure
                        | None ->
                            return
                                Ok
                                    {
                                        Items = items
                                        NextAfterVersion = next
                                    }
            })

    let private visibleListRows connection transaction (context: ActorCallContext) after limit =
        task {
            let kind, issuer, stable = PrincipalKey.storageParts context.Binding.Principal

            let roles =
                ActorAuthorization.rolesForCapability Capability.ListCases
                |> List.map ActorGrantCandidate.roleName
                |> List.toArray

            use command = new NpgsqlCommand(Sql.listVisibleCases, connection, transaction)
            Sql.optional command "after" NpgsqlDbType.Text after
            Sql.uuid command "actor" context.Binding.ActorId
            Sql.text command "kind" kind
            Sql.text command "issuer" issuer
            Sql.text command "principal" stable
            Sql.add command "roles" (NpgsqlDbType.Array ||| NpgsqlDbType.Text) (box roles)
            Sql.add command "window" NpgsqlDbType.Integer (box (limit + 1))
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let rows = ResizeArray<Claim>()
            let mutable reading = true

            while reading do
                let! exists = reader.ReadAsync()
                reading <- exists

                if exists then
                    rows.Add(Rows.claim reader)

            return rows |> Seq.toList
        }

    let private listAtRevision
        connection
        transaction
        revision
        (context: ActorCallContext)
        (protection: ICaseListCursorProtection)
        (clock: IBusinessTime)
        (request: CaseListRequest)
        witness
        =
        task {
            if revision <> context.Binding.GrantRevision then
                return Error CoreFailure.ResourceUnavailable
            else
                let position =
                    request.AfterCursor
                    |> Option.map (fun token ->
                        CaseListCursorCodec.decode
                            protection
                            context.Binding
                            revision
                            request.Limit
                            (clock.Capture().ObservedUtcInstant)
                            token
                        |> Result.map Some)
                    |> Option.defaultValue (Ok None)

                match position with
                | Error() -> return Error CoreFailure.InvalidCaseListCursor
                | Ok after ->
                    let! rows = visibleListRows connection transaction context after request.Limit
                    let items = rows |> List.truncate request.Limit

                    let next =
                        if rows.Length > request.Limit then
                            items
                            |> List.tryLast
                            |> Option.map (fun value ->
                                CaseListCursorCodec.encode
                                    protection
                                    context.Binding
                                    revision
                                    request.Limit
                                    (clock.Capture().ObservedUtcInstant)
                                    (Claim.view value).Fields.CaseReference)
                        else
                            None

                    let page: CasePage = { Items = items; NextCursor = next }

                    match CaseReadEvidence.page witness connection transaction items with
                    | None -> return Ok page
                    | Some failure -> return Error failure
        }

    let list dataSource witness context protection clock request =
        snapshot dataSource (fun connection transaction revision ->
            listAtRevision connection transaction revision context protection clock request witness)
