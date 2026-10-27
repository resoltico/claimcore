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
        | StoreFailure.Cancelled -> ReadReply.Failed CoreFault.StoreUnavailable
        | value -> ReadReply.Failed(fault value)

    let private unavailable<'Value> : ReadReply<'Value> =
        ReadReply.Rejected Refusal.ResourceUnavailable

    let private validLimit limit =
        limit >= 1 && limit <= Paging.MaximumPageSize

    let private invalidLimit () =
        ReadReply.Rejected(
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

    let private succeeded result =
        result |> Result.map ReadReply.Succeeded |> Result.defaultWith ReadReply.Failed

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

    let caseRecord installation principal actor record =
        Advice.caseRecord installation principal actor record

    let caseResult installation principal (view: CaseFacts) =
        if not (CaseExecution.authorize principal view.Actor Capability.ReadCase view.Case) then
            unavailable
        else
            match view.Case with
            | Some record -> caseRecord installation principal view.Actor record |> succeeded
            | None -> ReadReply.Failed CoreFault.StoreIntegrityError

    let get (store: IStore) installation principal reference token =
        task {
            match parseReference reference with
            | Error refusal -> return ReadReply.Rejected refusal
            | Ok _ ->
                let! result =
                    store.Read(
                        (fun tx ->
                            task {
                                let! view =
                                    ExecutionFacts.case tx installation principal None reference

                                return caseResult installation principal view
                            }),
                        token
                    )

                return readResult result
        }

    let contextResult productVersion (calendar: IBusinessCalendar) principal observedAt actor =
        match currentActor principal actor with
        | Error _ -> unavailable
        | Ok _ ->
            ReadReply.Succeeded
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
                            Authority.can principal actor capability ResourceScope.Installation)
                }

    let context productVersion (store: IStore) calendar principal token =
        task {
            let! result =
                store.Read(
                    (fun tx ->
                        task {
                            let! actor = tx.Authority(principal, None)

                            return
                                contextResult productVersion calendar principal tx.ObservedAt actor
                        }),
                    token
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

    let list (store: IStore) installation principal (request: PageRequest) token =
        task {
            if not (validLimit request.Limit) then
                return invalidLimit ()
            else
                let! result =
                    store.Read(
                        (fun tx ->
                            task {
                                let! actor = tx.Authority(principal, None)

                                match selectCases principal request actor with
                                | Error refusal -> return ReadReply.Rejected refusal
                                | Ok selection ->
                                    let actorId =
                                        actor.Actor
                                        |> Option.map _.ActorId
                                        |> Option.defaultValue Guid.Empty

                                    let! page =
                                        tx.CasePage(
                                            selection.Visibility,
                                            actorId,
                                            Authority.readRoles principal,
                                            Authority.invalidRoles principal,
                                            selection.AfterReference,
                                            request.Limit
                                        )

                                    if page.Items.Length > Paging.MaximumPageSize then
                                        return ReadReply.Failed CoreFault.StoreIntegrityError
                                    else
                                        return
                                            page.Items
                                            |> List.map (summary installation)
                                            |> collect
                                            |> Result.map (fun items ->
                                                {
                                                    CaseSummaryPage.Items = items
                                                    Offers =
                                                        Advice.installationOffers principal actor
                                                    NextCursor =
                                                        page.Next
                                                        |> Option.map Cursors.encodeCases
                                                })
                                            |> succeeded
                            }),
                        token
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

    let private historyEntry installation principal actor current detail entry =
        if detail = HistoryDetail.Summary then
            LedgerEntries.summary current entry |> Result.map HistoryEntry.SummaryEntry
        else
            LedgerEntries.validate installation entry
            |> Result.bind (LedgerEntries.receipt principal actor (Some current))
            |> Result.map HistoryEntry.FullEntry

    let history (store: IStore) installation principal (request: HistoryRequest) token =
        task {
            match parseReference request.CaseReference with
            | Error refusal -> return ReadReply.Rejected refusal
            | Ok _ when not (validLimit request.Limit) -> return invalidLimit ()
            | Ok _ ->
                let! result =
                    store.Read(
                        (fun tx ->
                            task {
                                let id = CaseReferenceId.encode installation request.CaseReference
                                let! actor = tx.Authority(principal, Some id)
                                let! record = tx.FindCase id

                                match selectHistory principal request actor record with
                                | Error refusal -> return ReadReply.Rejected refusal
                                | Ok after ->
                                    match record with
                                    | None -> return ReadReply.Failed CoreFault.StoreIntegrityError
                                    | Some current ->
                                        let! page = tx.HistoryPage(id, after, request.Limit)

                                        return
                                            CaseExecution.current installation current
                                            |> Result.bind (fun _ ->
                                                page.Items
                                                |> List.map (
                                                    historyEntry
                                                        installation
                                                        principal
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
                            }),
                        token
                    )

                return readResult result
        }

    let observe (store: IStore) installation principal operation token =
        task {
            if operation = Guid.Empty then
                return
                    ReadReply.Rejected(
                        Refusal.InvalidInput(InputTarget.OperationId, Violation.OperationIdRequired)
                    )
            else
                let! result =
                    store.Read(
                        (fun tx ->
                            task {
                                let! actor, entry, current =
                                    ExecutionFacts.operation tx principal operation

                                match entry with
                                | Some entry when
                                    LedgerEntries.canObserve principal actor entry.Subject
                                    ->
                                    if entry.OperationId <> operation then
                                        return ReadReply.Failed CoreFault.StoreIntegrityError
                                    else
                                        return
                                            LedgerEntries.validate installation entry
                                            |> Result.bind (
                                                LedgerEntries.receipt principal actor current
                                            )
                                            |> succeeded
                                | _ -> return unavailable
                            }),
                        token
                    )

                return readResult result
        }

    let selectActors principal (request: PageRequest) actor =
        if
            not (Authority.can principal actor Capability.ReadAuthority ResourceScope.Installation)
        then
            Error Refusal.ResourceUnavailable
        else
            currentActor principal actor
            |> Result.bind (fun _ -> cursor Cursors.decodeActors request.AfterCursor)

    let actors (store: IStore) principal (request: PageRequest) token =
        task {
            if not (validLimit request.Limit) then
                return invalidLimit ()
            else
                let! result =
                    store.Read(
                        (fun tx ->
                            task {
                                let! actor = tx.Authority(principal, None)

                                match selectActors principal request actor with
                                | Error refusal -> return ReadReply.Rejected refusal
                                | Ok after ->
                                    let! page = tx.ActorPage(after, request.Limit)

                                    if page.Items.Length > Paging.MaximumPageSize then
                                        return ReadReply.Failed CoreFault.StoreIntegrityError
                                    else
                                        return
                                            ReadReply.Succeeded
                                                {
                                                    ActorReviewPage.Items =
                                                        page.Items |> List.map Actor.describe
                                                    NextCursor =
                                                        page.Next
                                                        |> Option.map Cursors.encodeActors
                                                }
                            }),
                        token
                    )

                return readResult result
        }

    let selectGrants
        principal
        (request: GrantsRequest)
        (actor: AuthorityView)
        (target: Actor option)
        =
        if
            not (Authority.can principal actor Capability.ReadAuthority ResourceScope.Installation)
        then
            Error Refusal.ResourceUnavailable
        else
            match currentActor principal actor, target with
            | Ok _, Some target when target.ActorId = request.ActorId ->
                cursor (Cursors.decodeGrants request.ActorId) request.AfterCursor
            | _ -> Error Refusal.ResourceUnavailable

    let grants (store: IStore) principal (request: GrantsRequest) token =
        task {
            if not (validLimit request.Limit) then
                return invalidLimit ()
            elif request.ActorId = Guid.Empty then
                return unavailable
            else
                let! result =
                    store.Read(
                        (fun tx ->
                            task {
                                let! actor = tx.Authority(principal, None)
                                let! target = tx.FindActor request.ActorId

                                match selectGrants principal request actor target with
                                | Error refusal -> return ReadReply.Rejected refusal
                                | Ok after ->
                                    match target with
                                    | None -> return ReadReply.Failed CoreFault.StoreIntegrityError
                                    | Some target ->
                                        let! page =
                                            tx.GrantPage(request.ActorId, after, request.Limit)

                                        if page.Items.Length > Paging.MaximumPageSize then
                                            return ReadReply.Failed CoreFault.StoreIntegrityError
                                        else
                                            return
                                                ReadReply.Succeeded
                                                    {
                                                        AuthorityGrantPage.Actor =
                                                            Actor.describe target
                                                        Items = page.Items
                                                        NextCursor =
                                                            page.Next
                                                            |> Option.map (
                                                                Cursors.encodeGrants
                                                                    request.ActorId
                                                            )
                                                    }
                            }),
                        token
                    )

                return readResult result
        }
