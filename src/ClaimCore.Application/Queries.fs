namespace ClaimCore.Application

open System
open System.Globalization
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.RecordFormat

module internal Queries =
    let fault =
        function
        | StoreFailure.Unavailable -> CoreFault.StoreUnavailable
        | StoreFailure.SchemaMismatch -> CoreFault.SchemaMismatch
        | _ -> CoreFault.StoreIntegrityError

    let failure =
        function
        | StoreFailure.Cancelled -> QueryOutcome.Cancelled
        | value -> QueryOutcome.Failed(fault value)

    let private rejected reason = QueryOutcome.Rejected reason

    let private unavailable<'value> : QueryOutcome<'value> =
        rejected Rejection.ResourceUnavailable

    let private maximum = ServiceLimits.MaximumPageSize
    let private validLimit limit = limit >= 1 && limit <= maximum

    let private invalidLimit () =
        rejected (Rejection.PageLimitOutOfRange maximum)

    let private collect results =
        results
        |> List.fold
            (fun state item ->
                state
                |> Result.bind (fun values -> item |> Result.map (fun value -> value :: values)))
            (Ok [])
        |> Result.map List.rev

    let private currentActor principal actor =
        match actor with
        | Some actor when ActorAuthorization.validActor principal actor -> Ok actor
        | _ -> Error Rejection.ResourceUnavailable

    let private offer definition reference revision choices : OperationOffer =
        {
            Definition = definition
            CaseReference = reference
            ExpectedRevision = revision
            Choices = choices
        }

    let private correctionChoices principal actor resource accepted =
        ActorAuthorization.correctionGroups
        |> List.map (fun (group, capability) ->
            let actions =
                if ActorAuthorization.can principal actor capability resource then
                    Case.correctionActions accepted group
                else
                    [ CorrectionGroupAction.Keep ]

            {
                Input = "groups." + CorrectionGroup.token group
                Values =
                    actions
                    |> List.map (function
                        | CorrectionGroupAction.Keep -> "KEEP"
                        | CorrectionGroupAction.Replace -> "REPLACE"
                        | CorrectionGroupAction.Clear -> "CLEAR")
            })

    let private historyDetails principal actor resource =
        [ "SUMMARY", Capability.ReadHistory; "FULL", Capability.ReadHistoryFull ]
        |> List.choose (fun (detail, capability) ->
            if ActorAuthorization.can principal actor capability resource then
                Some detail
            else
                None)

    let private historyOffers reference details =
        if List.isEmpty details then
            []
        else
            [
                offer
                    "case.history"
                    (Some reference)
                    None
                    [ { Input = "detail"; Values = details } ]
            ]

    let private caseOffer principal actor resource accepted (snapshot: CaseSnapshot) kind =
        let choices =
            if kind = (CaseActKind.Business BusinessActKind.CorrectCase) then
                correctionChoices principal actor resource accepted
            else
                []

        if
            kind = (CaseActKind.Business BusinessActKind.CorrectCase)
            && (choices |> List.forall (fun choice -> choice.Values = [ "KEEP" ]))
        then
            None
        else
            Some(
                kind,
                offer
                    (CaseActKinds.token kind)
                    (Some snapshot.Reference)
                    (Some snapshot.Revision)
                    choices
            )

    let caseRecord installationId principal actor (record: StoredCase) =
        CaseDecisions.current installationId record
        |> Result.map (fun accepted ->
            let resource = ResourceScope.Case record.CaseId

            let acts =
                Case.eligibleActs accepted
                |> List.filter (ActorAuthorization.canAdvertise principal actor resource)

            let details = historyDetails principal actor resource

            let offered =
                acts
                |> List.choose (caseOffer principal actor resource accepted record.Snapshot)

            {
                Snapshot = record.Snapshot
                AvailableActs = offered |> List.map fst
                Offers =
                    (offered |> List.map snd) @ historyOffers record.Snapshot.Reference details
                HistoryDetails = details
            })

    let private installationOffers principal actor =
        let can capability =
            ActorAuthorization.can principal actor capability ResourceScope.Installation

        [
            if can Capability.Register then
                yield offer "OPEN" None (Some 0L) []
            if can Capability.ManageAuthority then
                for definition in AuthorityOperationDefinitions.all do
                    yield offer (RecordedAuthorityActs.token definition.Kind) None None []
            if can Capability.ReadAuthority then
                yield offer "authority.list" None None []
                yield offer "authority.grants" None None []
                yield offer "operation.observe" None None []
            match actor with
            | Some authority when ActorAuthorization.visibility principal authority |> Option.isSome ->
                yield offer "case.list" None None []
                yield offer "case.get" None None []

                if not (can Capability.ReadAuthority) then
                    yield offer "operation.observe" None None []
            | _ -> ()
        ]

    let get
        (store: ILedgerStore)
        installationId
        principal
        reference
        ct
        : Task<QueryOutcome<CaseReadModel>> =
        task {
            match CaseReference.validate reference with
            | Error error -> return rejected (Rejection.Case error)
            | Ok() ->
                let! read = store.ReadCase(principal, None, reference, ct)

                match read with
                | Error error -> return failure error
                | Ok view when
                    not (CaseDecisions.authorize principal view.Actor Capability.ReadCase view.Case)
                    ->
                    return unavailable
                | Ok view ->
                    return
                        caseRecord installationId principal view.Actor (Option.get view.Case)
                        |> Result.map QueryOutcome.Succeeded
                        |> Result.defaultWith QueryOutcome.Failed
        }

    let context
        (store: ILedgerStore)
        (calendar: IBusinessCalendar)
        principal
        ct
        : Task<QueryOutcome<ActorContext>> =
        task {
            let! read = store.ReadActor(principal, ct)

            match read with
            | Error error -> return failure error
            | Ok view ->
                match currentActor principal view.Actor with
                | Error _ -> return unavailable
                | Ok _ ->
                    return
                        QueryOutcome.Succeeded
                            {
                                Runtime =
                                    {
                                        ProductVersion = BuildIdentity.current.Version
                                        EffectiveBusinessDate = calendar.DateAt view.ObservedAt
                                        TimeZoneId = calendar.TimeZoneId
                                    }
                                Offers = installationOffers principal view.Actor
                                Capabilities =
                                    Capabilities.all
                                    |> List.filter (fun capability ->
                                        ActorAuthorization.can
                                            principal
                                            view.Actor
                                            capability
                                            ResourceScope.Installation)
                            }
        }

    let private cursor decode token =
        match token with
        | None -> Ok None
        | Some token -> decode token |> Result.map Some

    let private selectCases principal (request: CaseListRequest) (view: ActorReadView) =
        currentActor principal view.Actor
        |> Result.bind (fun actor ->
            match ActorAuthorization.visibility principal actor with
            | None -> Error Rejection.ResourceUnavailable
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
        | Ok _ when page.Items.Length > ServiceLimits.MaximumPageSize ->
            Error CoreFault.StoreIntegrityError
        | Ok _ ->
            page.Items
            |> List.map (summary installationId)
            |> collect
            |> Result.map (fun items ->
                {
                    Items = items
                    Offers = installationOffers principal page.Actor
                    NextCursor =
                        page.NextAfterReference
                        |> Option.map (fun reference -> Cursors.encodeCases reference)
                })

    let list
        (store: ILedgerStore)
        installationId
        principal
        (request: CaseListRequest)
        ct
        : Task<QueryOutcome<CaseSummaryPage>> =
        task {
            if not (validLimit request.Limit) then
                return invalidLimit ()
            else
                let! read =
                    store.ListCases(principal, selectCases principal request, request.Limit, ct)

                return
                    match read with
                    | Error error -> failure error
                    | Ok(Error reason) -> rejected reason
                    | Ok(Ok page) ->
                        casePage installationId principal page
                        |> Result.map QueryOutcome.Succeeded
                        |> Result.defaultWith QueryOutcome.Failed
        }

    let private selectHistory principal (request: HistoryRequest) (view: HistoryReadView) =
        let capability =
            if request.Detail = HistoryDetail.Full then
                Capability.ReadHistoryFull
            else
                Capability.ReadHistory

        if not (CaseDecisions.authorize principal view.Actor capability view.Case) then
            Error Rejection.ResourceUnavailable
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
        : Task<QueryOutcome<HistoryResultPage>> =
        task {
            match CaseReference.validate request.CaseReference with
            | Error error -> return rejected (Rejection.Case error)
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
                        |> Result.map QueryOutcome.Succeeded
                        |> Result.defaultWith QueryOutcome.Failed
        }

    let observe
        (store: ILedgerStore)
        installationId
        principal
        operationId
        ct
        : Task<QueryOutcome<OperationReceipt>> =
        task {
            if operationId = Guid.Empty then
                return
                    rejected (
                        Rejection.Case(
                            CaseRefusal.InvalidInput(
                                InputTarget.OperationId,
                                InputViolation.Act ActViolation.EmptyOperationId
                            )
                        )
                    )
            else
                let! read = store.ReadOperation(principal, operationId, ct)

                match read with
                | Error error -> return failure error
                | Ok view ->
                    match view.Entry with
                    | Some entry when LedgerEntries.canObserve principal view.Actor entry.Scope ->
                        if entry.OperationId <> operationId then
                            return QueryOutcome.Failed CoreFault.StoreIntegrityError
                        else
                            return
                                LedgerEntries.validate installationId entry
                                |> Result.bind (
                                    LedgerEntries.receipt principal view.Actor view.Case
                                )
                                |> Result.map QueryOutcome.Succeeded
                                |> Result.defaultWith QueryOutcome.Failed
                    | _ -> return unavailable
        }

    let private selectActors principal (request: AuthorityListRequest) (view: ActorReadView) =
        if
            not (
                ActorAuthorization.can
                    principal
                    view.Actor
                    Capability.ReadAuthority
                    ResourceScope.Installation
            )
        then
            Error Rejection.ResourceUnavailable
        else
            currentActor principal view.Actor
            |> Result.bind (fun _ -> cursor Cursors.decodeActors request.AfterCursor)

    let private actorPage principal (page: StoredActorPage) : Result<ActorReviewPage, CoreFault> =
        currentActor principal page.Actor
        |> Result.mapError (fun _ -> CoreFault.StoreIntegrityError)
        |> Result.bind (fun _ ->
            if page.Items.Length > ServiceLimits.MaximumPageSize then
                Error CoreFault.StoreIntegrityError
            else
                Ok
                    {
                        Items = page.Items
                        NextCursor =
                            page.NextAfterActorId |> Option.map (fun id -> Cursors.encodeActors id)
                    })

    let actors
        (store: ILedgerStore)
        principal
        (request: AuthorityListRequest)
        ct
        : Task<QueryOutcome<ActorReviewPage>> =
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
                        |> Result.map QueryOutcome.Succeeded
                        |> Result.defaultWith QueryOutcome.Failed
        }

    let private selectGrants principal (request: AuthorityGrantListRequest) (view: GrantReadView) =
        if
            not (
                ActorAuthorization.can
                    principal
                    view.Actor
                    Capability.ReadAuthority
                    ResourceScope.Installation
            )
        then
            Error Rejection.ResourceUnavailable
        else
            match currentActor principal view.Actor, view.Target with
            | Ok _, Some target when target.ActorId = request.ActorId ->
                cursor (Cursors.decodeGrants request.ActorId) request.AfterCursor
            | _ -> Error Rejection.ResourceUnavailable

    let private grantPage principal (request: AuthorityGrantListRequest) (page: StoredGrantPage) =
        currentActor principal page.Actor
        |> Result.mapError (fun _ -> CoreFault.StoreIntegrityError)
        |> Result.bind (fun _ ->
            if
                page.Target.ActorId <> request.ActorId
                || page.Items.Length > ServiceLimits.MaximumPageSize
            then
                Error CoreFault.StoreIntegrityError
            else
                Ok
                    {
                        Actor = page.Target
                        Items = page.Items
                        NextCursor =
                            page.NextAfterGrant
                            |> Option.map (fun position ->
                                Cursors.encodeGrants request.ActorId position)
                    })

    let grants
        (store: ILedgerStore)
        principal
        (request: AuthorityGrantListRequest)
        ct
        : Task<QueryOutcome<AuthorityGrantPage>> =
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
                        |> Result.map QueryOutcome.Succeeded
                        |> Result.defaultWith QueryOutcome.Failed
        }
