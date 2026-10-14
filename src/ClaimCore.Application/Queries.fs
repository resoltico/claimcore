namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain

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

    let private unavailable<'value> : QueryOutcome<'value> =
        QueryOutcome.Rejected Rejection.ResourceUnavailable

    let private validateReference reference =
        Claim.validateReference reference |> Result.mapError Rejection.Domain

    let private limit = SemanticContract.current.MaximumPageSize

    let private available principal actor (case: StoredCase) =
        match case.State with
        | StoredCaseState.Live(view, CaseDisposition.Active) when
            ActorAuthorization.can
                principal
                actor
                Capability.EditCase
                (ResourceScope.Case case.CaseId)
            ->
            Claim.restore view
            |> Result.map Claim.availableCommands
            |> Result.map (fun names ->
                CommandKinds.all
                |> List.filter (fun kind -> List.contains (CommandKinds.token kind) names))
            |> Result.defaultValue []
        | _ -> []

    let caseRecord principal actor case =
        match case.State with
        | StoredCaseState.Erased -> CaseRecord.Erased(case.Reference, case.Revision)
        | StoredCaseState.Live(view, disposition) ->
            CaseRecord.Live(view, disposition, available principal actor case)

    let get
        (store: ILedgerStore)
        principal
        reference
        ct
        : Task<QueryOutcome<Lookup<CaseRecord, string>>> =
        task {
            match validateReference reference with
            | Error error -> return QueryOutcome.Rejected error
            | Ok() ->
                let! read = store.ReadCase(principal, None, reference, ct)

                match read with
                | Error error -> return failure error
                | Ok view when
                    not (
                        CaseDecisions.authorize
                            principal
                            view.Actor
                            Capability.ReadCase
                            false
                            view.Case
                    )
                    ->
                    return unavailable
                | Ok view ->
                    return
                        QueryOutcome.Succeeded(
                            Lookup.Found(caseRecord principal view.Actor (Option.get view.Case))
                        )
        }

    let definition
        (store: ILedgerStore)
        (calendar: IBusinessCalendar)
        principal
        ct
        : Task<QueryOutcome<CoreDescription>> =
        task {
            let! read = store.ReadActor(principal, ct)

            match read with
            | Error error -> return failure error
            | Ok view when
                not (
                    ActorAuthorization.can
                        principal
                        view.Actor
                        Capability.ReadDefinition
                        ResourceScope.Definition
                )
                ->
                return unavailable
            | Ok view ->
                return
                    QueryOutcome.Succeeded
                        {
                            Contract = SemanticContract.current
                            SemanticFingerprint =
                                SemanticContract.fingerprint SemanticContract.current
                            Runtime =
                                {
                                    ProductVersion = BuildIdentity.current.Version
                                    EffectiveBusinessDate = calendar.DateAt view.ObservedAt
                                    TimeZoneId = calendar.TimeZoneId
                                }
                        }
        }

    let private summary case =
        match case.State with
        | StoredCaseState.Erased -> CaseSummary.Erased(case.Reference, case.Revision)
        | StoredCaseState.Live(view, disposition) ->
            CaseSummary.Live(case.Reference, case.Revision, view.Fields.Status, disposition)

    let list
        (store: ILedgerStore)
        principal
        (request: CaseListRequest)
        ct
        : Task<QueryOutcome<CaseSummaryPage>> =
        task {
            let cursor =
                request.AfterCursor
                |> Option.map CaseListCursor.decode
                |> Option.defaultValue (Ok "")

            if request.Limit < 1 || request.Limit > limit then
                return QueryOutcome.Rejected(Rejection.PageLimitOutOfRange limit)
            else
                match cursor with
                | Error() -> return QueryOutcome.Rejected Rejection.InvalidCaseListCursor
                | Ok value ->
                    let! read =
                        store.ListCases(
                            principal,
                            ActorAuthorization.visibility principal,
                            (if value = "" then None else Some value),
                            request.Limit,
                            ct
                        )

                    match read with
                    | Error error -> return failure error
                    | Ok None -> return unavailable
                    | Ok(Some page) ->
                        return
                            QueryOutcome.Succeeded
                                {
                                    Items = List.map summary page.Items
                                    NextCursor =
                                        page.NextAfterReference |> Option.map CaseListCursor.encode
                                    AvailableCommands =
                                        if
                                            ActorAuthorization.can
                                                principal
                                                page.Actor
                                                Capability.EditCase
                                                ResourceScope.Installation
                                        then
                                            [ CommandKind.Open ]
                                        else
                                            []
                                }
        }

    let private historyEntry full (entry: StoredEntry) =
        match entry.Scope with
        | StoredScope.Authority _ -> Error CoreFault.StoreIntegrityError
        | StoredScope.Case(_, revision, act, _) ->
            if full && entry.Payload.IsSome then
                LedgerEntries.receipt entry |> Result.map HistoryEntry.FullEntry
            else
                Ok(
                    HistoryEntry.SummaryEntry
                        {
                            OperationId = entry.OperationId
                            Position = LedgerEntries.position entry.Sequence entry.EntryHash
                            Revision = revision
                            Act = act
                            RecordedAt = entry.RecordedAt
                            RecordedBy = entry.ActorId
                        }
                )

    let private historyPage detail (page: StoredHistoryPage) =
        let erased =
            page.Case
            |> Option.exists (fun value ->
                match value.State with
                | StoredCaseState.Erased -> true
                | _ -> false)

        let entries =
            page.Entries
            |> List.map (historyEntry (detail = HistoryDetail.Full && not erased))

        match
            entries
            |> List.tryPick (function
                | Error error -> Some error
                | _ -> None)
        with
        | Some error -> Error error
        | None ->
            Ok
                {
                    Entries =
                        entries
                        |> List.choose (function
                            | Ok value -> Some value
                            | _ -> None)
                    NextCursor = page.NextAfterRevision |> Option.map HistoryCursor.encode
                }

    let history
        (store: ILedgerStore)
        principal
        (request: HistoryRequest)
        ct
        : Task<QueryOutcome<Lookup<HistoryResultPage, string>>> =
        task {
            let cursor =
                request.AfterCursor
                |> Option.map HistoryCursor.decode
                |> Option.defaultValue (Ok 0L)

            match validateReference request.CaseReference, cursor with
            | Error error, _ -> return QueryOutcome.Rejected error
            | _, Error() -> return QueryOutcome.Rejected Rejection.InvalidHistoryCursor
            | Ok(), Ok after ->
                if request.Limit < 1 || request.Limit > limit then
                    return QueryOutcome.Rejected(Rejection.PageLimitOutOfRange limit)
                else
                    let! read =
                        store.ReadHistory(
                            principal,
                            request.CaseReference,
                            after,
                            request.Limit,
                            ct
                        )

                    let capability =
                        if request.Detail = HistoryDetail.Full then
                            Capability.ReadHistoryFull
                        else
                            Capability.ReadHistorySummary

                    match read with
                    | Error error -> return failure error
                    | Ok page when
                        not (
                            CaseDecisions.authorize principal page.Actor capability false page.Case
                        )
                        ->
                        return unavailable
                    | Ok page ->
                        return
                            historyPage request.Detail page
                            |> Result.map (Lookup.Found >> QueryOutcome.Succeeded)
                            |> Result.defaultWith QueryOutcome.Failed
        }

    let observe
        (store: ILedgerStore)
        principal
        operationId
        ct
        : Task<QueryOutcome<Lookup<OperationReceipt, Guid>>> =
        task {
            if operationId = Guid.Empty then
                return
                    QueryOutcome.Rejected(
                        Rejection.Domain(
                            DomainError.InvalidInput(
                                InputTarget.OperationId,
                                InputViolation.Command CommandViolation.EmptyOperationId
                            )
                        )
                    )
            else
                let! read = store.ReadOperation(principal, operationId, ct)

                match read with
                | Error error -> return failure error
                | Ok(actor, Some entry, _) when
                    (match entry.Scope with
                     | StoredScope.Case _ -> true
                     | _ -> false)
                    && LedgerEntries.canObserve principal actor entry.Scope
                    && entry.Payload.IsSome
                    ->
                    return
                        LedgerEntries.receipt entry
                        |> Result.map (Lookup.Found >> QueryOutcome.Succeeded)
                        |> Result.defaultWith QueryOutcome.Failed
                | _ -> return unavailable
        }
