namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Every read authorizes and decodes its continuation inside one store snapshot.
module internal Queries =
    let fault =
        function
        | StoreFailure.Internal -> CoreFault.InternalError
        | StoreFailure.Unavailable -> CoreFault.StoreUnavailable
        | StoreFailure.SchemaMismatch -> CoreFault.SchemaMismatch
        | _ -> CoreFault.StoreIntegrityError

    let failure =
        function
        | StoreFailure.Cancelled -> Outcome.Failed CoreFault.StoreUnavailable
        | value -> Outcome.Failed(fault value)

    let private unavailable<'Value> : Outcome<'Value> =
        Outcome.Rejected Refusal.ResourceUnavailable

    let private validLimit limit =
        limit >= 1 && limit <= Paging.MaximumPageSize

    let private invalidLimit () =
        Outcome.Rejected(
            Refusal.InvalidInput(InputTarget.Limit, Violation.PageLimitRange Paging.MaximumPageSize)
        )

    let private parseReference value =
        CaseReference.parse value
        |> Result.mapError (fun violation ->
            Refusal.InvalidInput(InputTarget.Field BusinessField.CaseReference, violation))

    let private currentActor principal actor =
        if Authority.validActor principal actor then
            Ok actor
        else
            Error Refusal.ResourceUnavailable

    let private readResult result = result |> Result.defaultWith failure

    let private succeeded result = Outcome.ofResult result

    let private collect results =
        List.foldBack
            (fun item rest ->
                Result.bind (fun value -> Result.map (fun rest -> value :: rest) rest) item)
            results
            (Ok [])

    let private cursor decode =
        function
        | None -> Ok None
        | Some value -> decode value |> Result.map Some

    let caseRecord (services: ServiceContext) actor record = Advice.caseRecord services actor record

    let caseResult (services: ServiceContext) (view: CaseFacts) =
        let principal = services.Principal

        if not (CaseExecution.authorize principal view.Actor Capability.ReadCase view.Case) then
            unavailable
        else
            match view.Case with
            | Some record -> caseRecord services view.Actor record |> succeeded
            | None -> Outcome.Failed CoreFault.StoreIntegrityError

    let get (services: ServiceContext) reference token =
        let store = services.Store

        task {
            match parseReference reference with
            | Error refusal -> return Outcome.Rejected refusal
            | Ok _ ->
                let! result =
                    store.Read(
                        {
                            ReadOperation.Work =
                                (fun tx ->
                                    task {
                                        let! view = ExecutionFacts.case services tx None reference

                                        return caseResult services view
                                    })
                            CancellationToken = token
                        }
                    )

                return readResult result
        }

    let contextResult (services: ServiceContext) observedAt actor =
        let productVersion = services.ProductVersion
        let calendar = services.Calendar
        let principal = services.Principal

        match currentActor principal actor with
        | Error _ -> unavailable
        | Ok _ ->
            Outcome.Succeeded
                {
                    Runtime =
                        {
                            ProductVersion = productVersion
                            EffectiveBusinessDate = calendar.DateAt observedAt
                            TimeZoneId = calendar.TimeZoneId
                        }
                    View = Advice.views principal actor
                    Offers = Advice.installationOffers principal actor
                    Capability =
                        Capability.all
                        |> List.filter (fun capability ->
                            Authority.can principal actor capability Scope.Installation)
                }

    let context (services: ServiceContext) token =
        let store = services.Store
        let principal = services.Principal

        task {
            let! result =
                store.Read(
                    {
                        ReadOperation.Work =
                            (fun tx ->
                                task {
                                    let! actor =
                                        tx.Authority(
                                            {
                                                AuthorityQuery.Principal = principal
                                                CaseId = None
                                            }
                                        )

                                    return contextResult services tx.ObservedAt actor
                                })
                        CancellationToken = token
                    }
                )

            return readResult result
        }

    let selectCases principal (request: PageRequest) actor =
        currentActor principal actor
        |> Result.bind (fun actor ->
            match Authority.visibility principal actor with
            | None -> Error Refusal.ResourceUnavailable
            | Some visibility ->
                cursor Cursors.decodeCases request.AfterCursor
                |> Result.map (fun after ->
                    {
                        Visibility = visibility
                        ActorId =
                            actor.Actor |> Option.map _.ActorId |> Option.defaultValue Guid.Empty
                        ReadRoles = Authority.readRoles principal
                        ForbiddenRoles = Authority.invalidRoles principal
                        Limit = request.Limit
                        AfterReference = after
                    }))

    let private summary installation record =
        CaseExecution.current installation record
        |> Result.map (fun _ ->
            {
                CaseSummary.Reference = record.Snapshot.Reference
                Revision = record.Snapshot.Revision
                State =
                    CaseState.map (fun (fields: CaseFields) -> fields.Status) record.Snapshot.State
            })

    let list (services: ServiceContext) (request: PageRequest) token =
        let store = services.Store
        let installation = services.InstallationId
        let principal = services.Principal

        task {
            if not (validLimit request.Limit) then
                return invalidLimit ()
            else
                let! result =
                    store.Read(
                        {
                            ReadOperation.Work =
                                (fun tx ->
                                    task {
                                        let! actor =
                                            tx.Authority(
                                                {
                                                    AuthorityQuery.Principal = principal
                                                    CaseId = None
                                                }
                                            )

                                        match selectCases principal request actor with
                                        | Error refusal -> return Outcome.Rejected refusal
                                        | Ok selection ->
                                            let! page = tx.CasePage selection

                                            if page.Items.Length > Paging.MaximumPageSize then
                                                return Outcome.Failed CoreFault.StoreIntegrityError
                                            else
                                                return
                                                    page.Items
                                                    |> List.map (summary installation)
                                                    |> collect
                                                    |> Result.map (fun items ->
                                                        {
                                                            CaseSummaryPage.Items = items
                                                            Offers =
                                                                Advice.installationOffers
                                                                    principal
                                                                    actor
                                                            NextCursor =
                                                                page.Next
                                                                |> Option.map Cursors.encodeCases
                                                        })
                                                    |> succeeded
                                    })
                            CancellationToken = token
                        }
                    )

                return readResult result
        }

    let selectHistory principal (request: HistoryRequest) actor record =
        let capability =
            if request.Detail = HistoryDetail.Full then
                Capability.ReadHistoryFull
            else
                Capability.ReadHistory

        if not (CaseExecution.authorize principal actor capability record) then
            Error Refusal.ResourceUnavailable
        else
            currentActor principal actor
            |> Result.bind (fun _ ->
                cursor (Cursors.decodeHistory request.CaseReference) request.AfterCursor
                |> Result.map (Option.defaultValue 0L))

    let private historyEntry (services: ServiceContext) actor current detail entry =
        let installation = services.InstallationId
        let principal = services.Principal

        if detail = HistoryDetail.Summary then
            LedgerEntries.summary current entry |> Result.map HistoryEntry.SummaryEntry
        else
            LedgerEntries.validate installation entry
            |> Result.bind (LedgerEntries.receipt principal actor (Some current))
            |> Result.map HistoryEntry.FullEntry

    let history (services: ServiceContext) (request: HistoryRequest) token =
        let store = services.Store
        let installation = services.InstallationId
        let principal = services.Principal

        task {
            match parseReference request.CaseReference with
            | Error refusal -> return Outcome.Rejected refusal
            | Ok _ when not (validLimit request.Limit) -> return invalidLimit ()
            | Ok _ ->
                let! result =
                    store.Read(
                        {
                            ReadOperation.Work =
                                (fun tx ->
                                    task {
                                        let id =
                                            CaseReferenceId.encode
                                                installation
                                                request.CaseReference

                                        let! actor =
                                            tx.Authority(
                                                {
                                                    AuthorityQuery.Principal = principal
                                                    CaseId = Some id
                                                }
                                            )

                                        let! record = tx.FindCase id

                                        match selectHistory principal request actor record with
                                        | Error refusal -> return Outcome.Rejected refusal
                                        | Ok after ->
                                            match record with
                                            | None ->
                                                return Outcome.Failed CoreFault.StoreIntegrityError
                                            | Some current ->
                                                let! page =
                                                    tx.HistoryPage(
                                                        {
                                                            HistoryPageQuery.CaseId = id
                                                            AfterRevision = after
                                                            Limit = request.Limit
                                                        }
                                                    )

                                                return
                                                    CaseExecution.current installation current
                                                    |> Result.bind (fun _ ->
                                                        page.Items
                                                        |> List.map (
                                                            historyEntry
                                                                services
                                                                actor
                                                                current
                                                                request.Detail
                                                        )
                                                        |> collect)
                                                    |> Result.map (fun entries ->
                                                        {
                                                            HistoryResultPage.Entries = entries
                                                            NextCursor =
                                                                page.Next
                                                                |> Option.map (
                                                                    Cursors.encodeHistory
                                                                        request.CaseReference
                                                                )
                                                        })
                                                    |> succeeded
                                    })
                            CancellationToken = token
                        }
                    )

                return readResult result
        }

    let observe (services: ServiceContext) operation token =
        let store = services.Store
        let installation = services.InstallationId
        let principal = services.Principal

        task {
            if operation = Guid.Empty then
                return
                    Outcome.Rejected(
                        Refusal.InvalidInput(InputTarget.OperationId, Violation.OperationIdRequired)
                    )
            else
                let! result =
                    store.Read(
                        {
                            ReadOperation.Work =
                                (fun tx ->
                                    task {
                                        let! actor, entry, current =
                                            ExecutionFacts.operation tx principal operation

                                        match entry with
                                        | Some entry when
                                            LedgerEntries.canObserve principal actor entry.Subject
                                            ->
                                            if entry.OperationId <> operation then
                                                return Outcome.Failed CoreFault.StoreIntegrityError
                                            else
                                                return
                                                    LedgerEntries.validate installation entry
                                                    |> Result.bind (
                                                        LedgerEntries.receipt
                                                            principal
                                                            actor
                                                            current
                                                    )
                                                    |> succeeded
                                        | _ -> return unavailable
                                    })
                            CancellationToken = token
                        }
                    )

                return readResult result
        }

    let selectActors principal (request: PageRequest) actor =
        if not (Authority.can principal actor Capability.ReadAuthority Scope.Installation) then
            Error Refusal.ResourceUnavailable
        else
            currentActor principal actor
            |> Result.bind (fun _ -> cursor Cursors.decodeActors request.AfterCursor)

    let actors (services: ServiceContext) (request: PageRequest) token =
        let store = services.Store
        let principal = services.Principal

        task {
            if not (validLimit request.Limit) then
                return invalidLimit ()
            else
                let! result =
                    store.Read(
                        {
                            ReadOperation.Work =
                                (fun tx ->
                                    task {
                                        let! actor =
                                            tx.Authority(
                                                {
                                                    AuthorityQuery.Principal = principal
                                                    CaseId = None
                                                }
                                            )

                                        match selectActors principal request actor with
                                        | Error refusal -> return Outcome.Rejected refusal
                                        | Ok after ->
                                            let! page =
                                                tx.ActorPage(
                                                    {
                                                        ActorPageQuery.AfterActorId = after
                                                        Limit = request.Limit
                                                    }
                                                )

                                            if page.Items.Length > Paging.MaximumPageSize then
                                                return Outcome.Failed CoreFault.StoreIntegrityError
                                            else
                                                return
                                                    Outcome.Succeeded
                                                        {
                                                            ActorReviewPage.Items = page.Items

                                                            NextCursor =
                                                                page.Next
                                                                |> Option.map Cursors.encodeActors
                                                        }
                                    })
                            CancellationToken = token
                        }
                    )

                return readResult result
        }

    let selectGrants
        principal
        (request: GrantsRequest)
        (actor: AuthorityView)
        (target: Actor option)
        =
        if not (Authority.can principal actor Capability.ReadAuthority Scope.Installation) then
            Error Refusal.ResourceUnavailable
        else
            match currentActor principal actor, target with
            | Ok _, Some target when target.ActorId = request.ActorId ->
                cursor (Cursors.decodeGrants request.ActorId) request.AfterCursor
            | _ -> Error Refusal.ResourceUnavailable

    let grants (services: ServiceContext) (request: GrantsRequest) token =
        let store = services.Store
        let principal = services.Principal

        task {
            if not (validLimit request.Limit) then
                return invalidLimit ()
            elif request.ActorId = Guid.Empty then
                return unavailable
            else
                let! result =
                    store.Read(
                        {
                            ReadOperation.Work =
                                (fun tx ->
                                    task {
                                        let! actor =
                                            tx.Authority(
                                                {
                                                    AuthorityQuery.Principal = principal
                                                    CaseId = None
                                                }
                                            )

                                        let! target = tx.FindActor request.ActorId

                                        match selectGrants principal request actor target with
                                        | Error refusal -> return Outcome.Rejected refusal
                                        | Ok after ->
                                            match target with
                                            | None ->
                                                return Outcome.Failed CoreFault.StoreIntegrityError
                                            | Some target ->
                                                let! page =
                                                    tx.GrantPage(
                                                        {
                                                            GrantPageQuery.ActorId =
                                                                request.ActorId
                                                            AfterGrant = after
                                                            Limit = request.Limit
                                                        }
                                                    )

                                                if page.Items.Length > Paging.MaximumPageSize then
                                                    return
                                                        Outcome.Failed
                                                            CoreFault.StoreIntegrityError
                                                else
                                                    return
                                                        Outcome.Succeeded
                                                            {
                                                                AuthorityGrantPage.Actor = target
                                                                Items = page.Items
                                                                NextCursor =
                                                                    page.Next
                                                                    |> Option.map (
                                                                        Cursors.encodeGrants
                                                                            request.ActorId
                                                                    )
                                                            }
                                    })
                            CancellationToken = token
                        }
                    )

                return readResult result
        }
