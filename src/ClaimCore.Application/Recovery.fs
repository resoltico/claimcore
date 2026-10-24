namespace ClaimCore.Application

open System
open System.Threading
open ClaimCore.Contract
open ClaimCore.Domain

/// Application owns stage authorization and the separation between operational confirmation and acceptance.
module internal Recovery =
    let private stage
        (store: ILedgerStore)
        installation
        principal
        (envelope: ExecutionEnvelope)
        token
        =
        match envelope.Input with
        | OperationInput.Case request ->
            match Case.validateRequest request with
            | Error refusal ->
                System.Threading.Tasks.Task.FromResult(
                    Ok(RecoveryStaged.Terminal(ExecuteResult.Rejected refusal, None))
                )
            | Ok() ->
                store.Recovery.StageCase(
                    principal,
                    envelope,
                    CaseDecisions.stage installation principal request,
                    token
                )
        | OperationInput.Authority input ->
            match
                AuthorityRequests.admit input
                |> Result.bind (fun request ->
                    AuthorityRequests.validate request |> Result.map (fun () -> request))
            with
            | Error refusal ->
                System.Threading.Tasks.Task.FromResult(
                    Ok(RecoveryStaged.Terminal(ExecuteResult.Rejected refusal, None))
                )
            | Ok request ->
                store.Recovery.StageAuthority(
                    principal,
                    request,
                    envelope,
                    AuthorityDecisions.stage installation principal request,
                    token
                )

    let execute (store: ILedgerStore) installation principal envelope accept token =
        task {
            let! staged = stage store installation principal envelope token

            let! recovery, outcome =
                task {
                    match staged with
                    | Error failure ->
                        // An unknown journal commit is not evidence that business execution began.
                        return
                            None,
                            ExecuteResult.Failed(
                                if
                                    failure = StoreFailure.CommitOutcomeUnknown
                                    || failure = StoreFailure.Cancelled
                                then
                                    CoreFault.StoreUnavailable
                                else
                                    Queries.fault failure
                            )
                    | Ok(RecoveryStaged.Terminal(result, recovery)) -> return recovery, result
                    | Ok(RecoveryStaged.Stored(id, actor, frozen)) ->
                        let! result = accept frozen.Input token
                        let! retained = store.Recovery.Retained(actor, id, token)

                        return
                            (match retained with
                             | Ok false -> None
                             | _ -> Some id),
                            result
                }

            return
                {
                    OperationId = OperationInput.operationId envelope.Input
                    RecoveryId = recovery
                    Outcome = outcome
                }
        }

    type private ActorFailure =
        | Unavailable
        | StoreFault of CoreFault

    let private storeFault =
        function
        | StoreFailure.Cancelled
        | StoreFailure.CommitOutcomeUnknown -> CoreFault.StoreUnavailable
        | failure -> Queries.fault failure

    let private actor (store: ILedgerStore) principal token =
        task {
            let! loaded = store.ReadActor(principal, token)

            return
                loaded
                |> Result.mapError (storeFault >> ActorFailure.StoreFault)
                |> Result.bind (fun view ->
                    if ActorAuthorization.validActor principal view.Actor then
                        Ok view.Actor.Actor.Value.ActorId
                    else
                        Error ActorFailure.Unavailable)
        }

    let forget store principal (request: ForgetRequest) token =
        task {
            let! current = actor store principal token

            match current with
            | Error ActorFailure.Unavailable ->
                return ReadReply.Rejected Refusal.ResourceUnavailable
            | Error(ActorFailure.StoreFault fault) -> return ReadReply.Failed fault
            | Ok id ->
                let! result = store.Recovery.Forget(id, request.RecoveryId, token)

                return
                    result
                    |> Result.map ReadReply.Succeeded
                    |> Result.defaultWith (storeFault >> ReadReply.Failed)
        }

    let retry
        store
        principal
        (request: RecoveryRequest)
        (execute:
            ExecutionEnvelope -> CancellationToken -> System.Threading.Tasks.Task<ExecuteReply>)
        token
        =
        task {
            let! current = actor store principal token

            let unavailable outcome =
                {
                    OperationId = request.OperationId
                    RecoveryId = Some request.RecoveryId
                    Outcome = outcome
                }

            match current with
            | Error ActorFailure.Unavailable ->
                return unavailable (ExecuteResult.Rejected Refusal.ResourceUnavailable)
            | Error(ActorFailure.StoreFault fault) ->
                return unavailable (ExecuteResult.Failed fault)
            | Ok id ->
                let! loaded = store.Recovery.Load(id, request, token)

                match loaded with
                | Error failure -> return unavailable (ExecuteResult.Failed(storeFault failure))
                | Ok None -> return unavailable (ExecuteResult.Rejected Refusal.ResourceUnavailable)
                | Ok(Some envelope) ->
                    let! reply = execute envelope token
                    let! retained = store.Recovery.Retained(id, request.RecoveryId, token)

                    return
                        { reply with
                            RecoveryId =
                                match retained with
                                | Ok false -> None
                                | _ -> Some request.RecoveryId
                        }
        }

    type private CurrentAuthority =
        | Case of CaseReadView
        | Authority of AuthorityView

    let private offers (store: ILedgerStore) principal (item: RecoveryItem) token =
        task {
            match item.Scope, item.CaseReference with
            | "CASE", Some reference ->
                let! read = store.ReadCase(principal, None, reference, token)

                return
                    read
                    |> Result.mapError storeFault
                    |> Result.map (fun view ->
                        let resource =
                            view.Case
                            |> Option.map (fun value -> ResourceScope.Case value.CaseId)
                            |> Option.defaultValue ResourceScope.Installation

                        if
                            ActorAuthorization.can
                                principal
                                view.Actor
                                Capability.ReadCase
                                resource
                        then
                            Some(item, CurrentAuthority.Case view)
                        else
                            None)
            | _ ->
                let! read = store.ReadActor(principal, token)

                return
                    read
                    |> Result.mapError storeFault
                    |> Result.map (fun view ->
                        if
                            ActorAuthorization.can
                                principal
                                view.Actor
                                Capability.ReadAuthority
                                ResourceScope.Installation
                        then
                            Some(item, CurrentAuthority.Authority view.Actor)
                        else
                            None)
        }

    let private canRetry principal authority input =
        match authority, input with
        | CurrentAuthority.Case view, OperationInput.Case request ->
            CaseDecisions.permission principal view.Actor request view.Case |> Result.isOk
        | CurrentAuthority.Authority view, OperationInput.Authority _ ->
            ActorAuthorization.can
                principal
                view
                Capability.ManageAuthority
                ResourceScope.Installation
        | _ -> false

    let private withRetry
        (store: ILedgerStore)
        actorId
        principal
        (item: RecoveryItem)
        authority
        token
        =
        task {
            let! body =
                store.Recovery.Load(
                    actorId,
                    {
                        RecoveryId = item.RecoveryId
                        OperationId = item.OperationId
                    },
                    token
                )

            return
                body
                |> Result.mapError storeFault
                |> Result.map (fun body ->
                    let retry =
                        body
                        |> Option.exists (fun envelope ->
                            canRetry principal authority envelope.Input)

                    { item with
                        Offers =
                            if retry then
                                [ RecoveryOffer.Retry; RecoveryOffer.Forget ]
                            else
                                [ RecoveryOffer.Forget ]
                    })
        }

    let rec private collectOffers store actorId principal rows token =
        task {
            match rows with
            | [] -> return Ok []
            | row :: rest ->
                let! visibility = offers store principal row token

                match visibility with
                | Error fault -> return Error fault
                | Ok None -> return! collectOffers store actorId principal rest token
                | Ok(Some(item, authority)) ->
                    let! item = withRetry store actorId principal item authority token

                    match item with
                    | Error fault -> return Error fault
                    | Ok item ->
                        let! rest = collectOffers store actorId principal rest token
                        return rest |> Result.map (fun rest -> item :: rest)
        }

    let private cursor =
        function
        | None -> Ok None
        | Some text -> Cursors.decodeRecovery text |> Result.map Some

    let pending (store: ILedgerStore) principal (request: PageRequest) token =
        task {
            let! current = actor store principal token

            match current with
            | Error ActorFailure.Unavailable ->
                return ReadReply.Rejected Refusal.ResourceUnavailable
            | Error(ActorFailure.StoreFault fault) -> return ReadReply.Failed fault
            | Ok id ->
                match cursor request.AfterCursor with
                | Error refusal -> return ReadReply.Rejected refusal
                | Ok after when request.Limit > 0 && request.Limit <= Limits.MaximumPageSize ->
                    let! loaded = store.Recovery.Pending(id, after, request.Limit + 1, token)

                    match loaded with
                    | Error failure -> return ReadReply.Failed(storeFault failure)
                    | Ok rows ->
                        let selected = rows |> List.truncate request.Limit
                        let! items = collectOffers store id principal selected token

                        let next =
                            if rows.Length > request.Limit then
                                selected
                                |> List.tryLast
                                |> Option.map (fun row -> Cursors.encodeRecovery row.RecoveryId)
                            else
                                None

                        return
                            items
                            |> Result.map (fun items ->
                                ReadReply.Succeeded(
                                    { Items = items; NextCursor = next }: RecoveryPage
                                ))
                            |> Result.defaultWith ReadReply.Failed
                | _ ->

                    return
                        ReadReply.Rejected(
                            Refusal.InvalidInput(
                                InputTarget.Limit,
                                Violation.PageLimitRange Limits.MaximumPageSize
                            )
                        )
        }
