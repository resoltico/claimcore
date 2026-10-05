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
    let snapshot (dataSource: NpgsqlDataSource) ct action =
        task {
            let! result =
                StoreData.read dataSource ct (fun connection ->
                    task {
                        use! transaction =
                            connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)

                        let! revision = ActorGrantRead.lockRevision connection transaction false ct

                        return! action connection transaction revision
                    })

            return result |> Result.bind id
        }

    let private caseId connection transaction reference ct =
        ActorGrantGateQueries.caseIdByReference connection transaction reference ct

    let allowed connection transaction revision (context: ActorCallContext) resource action ct =
        task {
            let! authority =
                ActorGrantRead.loadUnderLock
                    connection
                    transaction
                    context.Binding.Principal
                    resource
                    revision
                    ct

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

    let get dataSource witness (context: ActorCallContext) reference ct =
        snapshot dataSource ct (fun connection transaction revision ->
            task {
                let! found = caseId connection transaction reference ct

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
                            ct

                    if not canRead then
                        return Error CoreFailure.ResourceUnavailable
                    else
                        use command = new NpgsqlCommand(Sql.selectCase, connection, transaction)
                        Sql.text command "reference" reference
                        let! result = command.ExecuteReaderAsync(ct)
                        use reader = result
                        let! exists = reader.ReadAsync(ct)
                        let value = if exists then Some(Rows.claim reader) else None
                        reader.Close()

                        match value with
                        | None -> return Ok None
                        | Some claim ->
                            let! proof =
                                CaseReadEvidence.current witness connection transaction claim ct

                            return proof |> Result.map (fun () -> Some claim)
            })

    let private verifyHistory witness connection transaction receipts ct =
        task {
            let mutable failure = None

            for receipt: Receipt in receipts do
                if failure.IsNone then
                    let! proof =
                        CaseReadEvidence.receipt
                            witness
                            connection
                            transaction
                            receipt.OperationId
                            ct

                    match proof with
                    | Ok() -> ()
                    | Error cause -> failure <- Some cause

            return failure
        }

    let private historyRows connection transaction reference afterVersion (ct: CancellationToken) =
        task {
            use command = new NpgsqlCommand(Sql.history, connection, transaction)
            Sql.text command "reference" reference
            Sql.integer command "after" afterVersion
            let! result = command.ExecuteReaderAsync(ct)
            use reader = result
            let rows = ResizeArray<Receipt>()
            let mutable reading = true

            while reading do
                let! exists = reader.ReadAsync(ct)
                reading <- exists

                if exists then
                    rows.Add(Rows.receipt reader false)

            reader.Close()
            return rows
        }

    let history
        dataSource
        witness
        (context: ActorCallContext)
        reference
        afterVersion
        (ct: CancellationToken)
        =
        snapshot dataSource ct (fun connection transaction revision ->
            task {
                let! found = caseId connection transaction reference ct

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
                            ct

                    if not canRead then
                        return Error CoreFailure.ResourceUnavailable
                    else
                        let! rows = historyRows connection transaction reference afterVersion ct
                        let limit = SemanticContract.current.MaximumPageSize
                        let items = rows |> Seq.truncate limit |> Seq.toList

                        let next =
                            if rows.Count > limit then
                                items
                                |> List.tryLast
                                |> Option.map (fun value -> (Claim.view value.Case).Version)
                            else
                                None

                        let! proof = verifyHistory witness connection transaction items ct

                        match proof with
                        | Some failure -> return Error failure
                        | None ->
                            return
                                Ok
                                    {
                                        Items = items
                                        NextAfterVersion = next
                                    }
            })

    let private visibleListRows
        connection
        transaction
        (context: ActorCallContext)
        after
        limit
        (ct: CancellationToken)
        =
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
            let! result = command.ExecuteReaderAsync(ct)
            use reader = result
            let rows = ResizeArray<Claim>()
            let mutable reading = true

            while reading do
                let! exists = reader.ReadAsync(ct)
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
        (request: CaseListRequest)
        witness
        (ct: CancellationToken)
        =
        task {
            if revision <> context.Binding.GrantRevision then
                return Error CoreFailure.ResourceUnavailable
            else
                let! instant = Sql.databaseNow connection transaction ct

                let position =
                    request.AfterCursor
                    |> Option.map (fun token ->
                        CaseListCursorCodec.decode
                            protection
                            context.Binding
                            revision
                            request.Limit
                            instant
                            token
                        |> Result.map Some)
                    |> Option.defaultValue (Ok None)

                match position with
                | Error() -> return Error CoreFailure.InvalidCaseListCursor
                | Ok after ->
                    let! rows =
                        visibleListRows connection transaction context after request.Limit ct

                    let page =
                        CaseListCursorCodec.page
                            protection
                            context.Binding
                            revision
                            request.Limit
                            instant
                            rows

                    let items = page.Items

                    let! proof = CaseReadEvidence.page witness connection transaction items ct

                    match proof with
                    | None -> return Ok page
                    | Some failure -> return Error failure
        }

    let list dataSource witness context protection request ct =
        snapshot dataSource ct (fun connection transaction revision ->
            listAtRevision connection transaction revision context protection request witness ct)
