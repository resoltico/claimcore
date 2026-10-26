namespace ClaimCore.Application

open ClaimCore.Contract

open System
open System.Globalization
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.RecordFormat

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

    let private rejected reason = ReadReply.Rejected reason

    let private unavailable<'value> : ReadReply<'value> =
        rejected Refusal.ResourceUnavailable

    let private maximum = Limits.MaximumPageSize
    let private validLimit limit = limit >= 1 && limit <= maximum

    let private invalidLimit () =
        rejected (Refusal.InvalidInput(InputTarget.Limit, Violation.PageLimitRange maximum))

    let private collect results =
        results
        |> List.fold
            (fun state item ->
                state
                |> Result.bind (fun values -> item |> Result.map (fun value -> value :: values)))
            (Ok [])
        |> Result.map List.rev

    let private currentActor principal actor =
        if ActorAuthorization.validActor principal actor then
            Ok actor
        else
            Error Refusal.ResourceUnavailable

    let caseRecord installationId principal actor record =
        Advice.caseRecord installationId principal actor record

    let caseResult installationId principal (view: CaseReadView) =
        if not (CaseDecisions.authorize principal view.Actor Capability.ReadCase view.Case) then
            unavailable
        else
            caseRecord installationId principal view.Actor (Option.get view.Case)
            |> Result.map ReadReply.Succeeded
            |> Result.defaultWith ReadReply.Failed

    let get
        (store: ILedgerStore)
        installationId
        principal
        reference
        ct
        : Task<ReadReply<CaseReadModel>> =
        task {
            match CaseReference.validate reference with
            | Error error -> return rejected (error)
            | Ok() ->
                let! read = store.ReadCase(principal, None, reference, ct)

                match read with
                | Error error -> return failure error
                | Ok view -> return caseResult installationId principal view
        }

    let contextResult (calendar: IBusinessCalendar) principal (view: ActorReadView) =
        match currentActor principal view.Actor with
        | Error _ -> unavailable
        | Ok _ ->
            ReadReply.Succeeded
                {
                    Runtime =
                        {
                            ProductVersion = BuildIdentity.current.Version
                            EffectiveBusinessDate = calendar.DateAt view.ObservedAt
                            TimeZoneId = calendar.TimeZoneId
                        }
                    Views = Advice.views principal view.Actor
                    Offers = Advice.installationOffers principal view.Actor
                    Capabilities =
                        Capabilities.all
                        |> List.filter (fun capability ->
                            ActorAuthorization.can
                                principal
                                view.Actor
                                capability
                                ResourceScope.Installation)
                }

    let context (store: ILedgerStore) calendar principal ct : Task<ReadReply<ActorContext>> =
        task {
            let! read = store.ReadActor(principal, ct)

            return
                match read with
                | Error error -> failure error
                | Ok view -> contextResult calendar principal view
        }

    let private cursor decode token =
        match token with
        | None -> Ok None
        | Some token -> decode token |> Result.map Some

    let selectCases principal (request: PageRequest) (view: ActorReadView) =
        currentActor principal view.Actor
        |> Result.bind (fun actor ->
            match ActorAuthorization.visibility principal actor with
            | None -> Error Refusal.ResourceUnavailable
            | Some visibility ->
                cursor Cursors.decodeCases request.AfterCursor
                |> Result.map (fun after ->
                    {
                        Visibility = visibility
                        AfterReference = after
                    }))

    let private summary installationId (record: StoredCase) =
        CaseDecisions.current installationId record
        |> Result.map (fun _ ->
            match record.Snapshot.View with
            | CaseView.Erased ->
                CaseSummary.Erased(record.Snapshot.Reference, record.Snapshot.Revision)
            | CaseView.Live(fields, disposition) ->
                CaseSummary.Live(
                    record.Snapshot.Reference,
                    record.Snapshot.Revision,
                    fields.Status,
                    disposition
                ))

    let private casePage
        installationId
        principal
        (page: StoredCasePage)
        : Result<CaseSummaryPage, CoreFault> =
        match currentActor principal page.Actor with
        | Error _ -> Error CoreFault.StoreIntegrityError
        | Ok _ when page.Items.Length > Limits.MaximumPageSize ->
            Error CoreFault.StoreIntegrityError
        | Ok _ ->
            page.Items
            |> List.map (summary installationId)
            |> collect
            |> Result.map (fun items ->
                {
                    Items = items
                    Offers = Advice.installationOffers principal page.Actor
                    NextCursor =
                        page.NextAfterReference
                        |> Option.map (fun reference -> Cursors.encodeCases reference)
                })

    let list
        (store: ILedgerStore)
        installationId
        principal
        (request: PageRequest)
        ct
        : Task<ReadReply<CaseSummaryPage>> =
        task {
            if not (validLimit request.Limit) then
                return invalidLimit ()
            else
                let! read =
                    store.ListCases(
                        principal,
                        ActorAuthorization.readRoles principal,
                        selectCases principal request,
                        request.Limit,
                        ct
                    )

                return
                    match read with
                    | Error error -> failure error
                    | Ok(Error reason) -> rejected reason
                    | Ok(Ok page) ->
                        casePage installationId principal page
                        |> Result.map ReadReply.Succeeded
                        |> Result.defaultWith ReadReply.Failed
        }

    let selectHistory principal (request: HistoryRequest) (view: HistoryReadView) =
        let capability =
            if request.Detail = HistoryDetail.Full then
                Capability.ReadHistoryFull
            else
                Capability.ReadHistory

        if not (CaseDecisions.authorize principal view.Actor capability view.Case) then
            Error Refusal.ResourceUnavailable
        else
            currentActor principal view.Actor
            |> Result.bind (fun _ ->
                cursor (Cursors.decodeHistory request.CaseReference) request.AfterCursor
                |> Result.map (Option.defaultValue 0L))

    let private historyEntry installationId principal actor current detail entry =
        if detail = HistoryDetail.Summary then
            LedgerEntries.summary current entry |> Result.map HistoryEntry.SummaryEntry
        else
            LedgerEntries.validate installationId entry
            |> Result.bind (LedgerEntries.receipt principal actor (Some current))
            |> Result.map HistoryEntry.FullEntry

    let private historyPage
        installationId
        principal
        (request: HistoryRequest)
        (page: StoredHistoryPage)
        =
        match currentActor principal page.Actor, page.Case with
        | Ok _, Some current ->
            CaseDecisions.current installationId current
            |> Result.bind (fun _ ->
                page.Entries
                |> List.map (
                    historyEntry installationId principal page.Actor current request.Detail
                )
                |> collect
                |> Result.map (fun entries ->
                    {
                        Entries = entries
                        NextCursor =
                            page.NextAfterRevision
                            |> Option.map (fun revision ->
                                Cursors.encodeHistory request.CaseReference revision)
                    }))
        | _ -> Error CoreFault.StoreIntegrityError

    let history
        (store: ILedgerStore)
        installationId
        principal
        (request: HistoryRequest)
        ct
        : Task<ReadReply<HistoryResultPage>> =
        task {
            match CaseReference.validate request.CaseReference with
            | Error error -> return rejected (error)
            | Ok() when not (validLimit request.Limit) -> return invalidLimit ()
            | Ok() ->
                let! read =
                    store.ReadHistory(
                        principal,
                        request.CaseReference,
                        selectHistory principal request,
                        request.Limit,
                        ct
                    )

                return
                    match read with
                    | Error error -> failure error
                    | Ok(Error reason) -> rejected reason
                    | Ok(Ok page) ->
                        historyPage installationId principal request page
                        |> Result.map ReadReply.Succeeded
                        |> Result.defaultWith ReadReply.Failed
        }

    let observe
        (store: ILedgerStore)
        installationId
        principal
        operationId
        ct
        : Task<ReadReply<OperationReceipt>> =
        task {
            if operationId = Guid.Empty then
                return
                    rejected (
                        (Refusal.InvalidInput(
                            InputTarget.OperationId,
                            Violation.OperationIdRequired
                        ))
                    )
            else
                let! read = store.ReadOperation(principal, operationId, ct)

                match read with
                | Error error -> return failure error
                | Ok view ->
                    match view.Entry with
                    | Some entry when LedgerEntries.canObserve principal view.Actor entry.Scope ->
                        if entry.OperationId <> operationId then
                            return ReadReply.Failed CoreFault.StoreIntegrityError
                        else
                            return
                                LedgerEntries.validate installationId entry
                                |> Result.bind (
                                    LedgerEntries.receipt principal view.Actor view.Case
                                )
                                |> Result.map ReadReply.Succeeded
                                |> Result.defaultWith ReadReply.Failed
                    | _ -> return unavailable
        }

    let selectActors principal (request: PageRequest) (view: ActorReadView) =
        if
            not (
                ActorAuthorization.can
                    principal
                    view.Actor
                    Capability.ReadAuthority
                    ResourceScope.Installation
            )
        then
            Error Refusal.ResourceUnavailable
        else
            currentActor principal view.Actor
            |> Result.bind (fun _ -> cursor Cursors.decodeActors request.AfterCursor)

    let private actorPage principal (page: StoredActorPage) : Result<ActorReviewPage, CoreFault> =
        currentActor principal page.Actor
        |> Result.mapError (fun _ -> CoreFault.StoreIntegrityError)
        |> Result.bind (fun _ ->
            if page.Items.Length > Limits.MaximumPageSize then
                Error CoreFault.StoreIntegrityError
            else
                Ok
                    {
                        Items = List.map Actor.describe page.Items
                        NextCursor =
                            page.NextAfterActorId |> Option.map (fun id -> Cursors.encodeActors id)
                    })

    let actors
        (store: ILedgerStore)
        principal
        (request: PageRequest)
        ct
        : Task<ReadReply<ActorReviewPage>> =
        task {
            if not (validLimit request.Limit) then
                return invalidLimit ()
            else
                let! read =
                    store.ListActors(principal, selectActors principal request, request.Limit, ct)

                return
                    match read with
                    | Error error -> failure error
                    | Ok(Error reason) -> rejected reason
                    | Ok(Ok page) ->
                        actorPage principal page
                        |> Result.map ReadReply.Succeeded
                        |> Result.defaultWith ReadReply.Failed
        }

    let selectGrants principal (request: GrantsRequest) (view: GrantReadView) =
        if
            not (
                ActorAuthorization.can
                    principal
                    view.Actor
                    Capability.ReadAuthority
                    ResourceScope.Installation
            )
        then
            Error Refusal.ResourceUnavailable
        else
            match currentActor principal view.Actor, view.Target with
            | Ok _, Some target when target.ActorId = request.ActorId ->
                cursor (Cursors.decodeGrants request.ActorId) request.AfterCursor
            | _ -> Error Refusal.ResourceUnavailable

    let private grantPage principal (request: GrantsRequest) (page: StoredGrantPage) =
        currentActor principal page.Actor
        |> Result.mapError (fun _ -> CoreFault.StoreIntegrityError)
        |> Result.bind (fun _ ->
            if
                page.Target.ActorId <> request.ActorId
                || page.Items.Length > Limits.MaximumPageSize
            then
                Error CoreFault.StoreIntegrityError
            else
                Ok
                    {
                        Actor = Actor.describe page.Target
                        Items = page.Items
                        NextCursor =
                            page.NextAfterGrant
                            |> Option.map (fun position ->
                                Cursors.encodeGrants request.ActorId position)
                    })

    let grants
        (store: ILedgerStore)
        principal
        (request: GrantsRequest)
        ct
        : Task<ReadReply<AuthorityGrantPage>> =
        task {
            if not (validLimit request.Limit) then
                return invalidLimit ()
            elif request.ActorId = Guid.Empty then
                return unavailable
            else
                let! read =
                    store.ListGrants(
                        principal,
                        request.ActorId,
                        selectGrants principal request,
                        request.Limit,
                        ct
                    )

                return
                    match read with
                    | Error error -> failure error
                    | Ok(Error reason) -> rejected reason
                    | Ok(Ok page) ->
                        grantPage principal request page
                        |> Result.map ReadReply.Succeeded
                        |> Result.defaultWith ReadReply.Failed
        }
