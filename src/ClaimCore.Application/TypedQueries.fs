namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain

module internal TypedQueries =
    let private limit = SemanticContract.current.MaximumPageSize

    let private invalid field message : Rejection =
        TypedProjection.rejection (DomainError.InvalidInput(field, message))

    let get
        (store: IClaimStore)
        (reference: string)
        (cancellationToken: CancellationToken)
        : Task<QueryOutcome<Lookup<CurrentCase, string>>> =
        if cancellationToken.IsCancellationRequested then
            Task.FromResult(QueryOutcome.Cancelled)
        else
            match Claim.validateReference reference with
            | Error rejection ->
                Task.FromResult(QueryOutcome.Rejected(TypedProjection.rejection rejection))
            | Ok() ->
                task {
                    let! result = store.Get reference

                    if cancellationToken.IsCancellationRequested then
                        return QueryOutcome.Cancelled
                    else
                        match result with
                        | Ok(Some claim) ->
                            return
                                QueryOutcome.Succeeded(
                                    Lookup.Found(TypedProjection.currentCase claim)
                                )
                        | Ok None -> return QueryOutcome.Succeeded(Lookup.NotFound reference)
                        | Error failure ->
                            return QueryOutcome.Failed(TypedProjection.coreFault failure)
                }

    let private casePage
        (request: CaseListRequest)
        (page: ClaimCore.Application.CasePage)
        : CaseSummaryPage =
        let items =
            page.Items
            |> List.map (
                Claim.view
                >> fun view ->
                    {
                        CaseReference = view.Fields.CaseReference
                        Revision = view.Version
                        Status = view.Fields.Status
                    }
            )

        let visible = items |> List.truncate request.Limit

        let next =
            if items.Length > visible.Length then
                visible |> List.tryLast |> Option.map (fun item -> item.CaseReference)
            else
                page.NextAfter

        {
            Items = visible
            NextAfterReference = next
        }

    let list
        (store: IClaimStore)
        (request: CaseListRequest)
        (cancellationToken: CancellationToken)
        : Task<QueryOutcome<CaseSummaryPage>> =
        let validAfter =
            request.AfterReference
            |> Option.map Claim.validateReference
            |> Option.defaultValue (Ok())

        if cancellationToken.IsCancellationRequested then
            Task.FromResult(QueryOutcome.Cancelled)
        elif request.Limit < 1 || request.Limit > limit then
            Task.FromResult(
                QueryOutcome.Rejected(invalid "limit" $"Use a value from 1 through {limit}.")
            )
        else
            match validAfter with
            | Error rejection ->
                Task.FromResult(QueryOutcome.Rejected(TypedProjection.rejection rejection))
            | Ok() ->
                task {
                    let! result = store.List request.AfterReference

                    if cancellationToken.IsCancellationRequested then
                        return QueryOutcome.Cancelled
                    else
                        match result with
                        | Error failure ->
                            return QueryOutcome.Failed(TypedProjection.coreFault failure)
                        | Ok page -> return QueryOutcome.Succeeded(casePage request page)
                }

    let private historyEntry (detail: HistoryDetail) (value: Receipt) : HistoryEntry =
        match detail with
        | HistoryDetail.Summary ->
            let view = Claim.view value.Case

            HistoryEntry.SummaryEntry
                {
                    OperationId = value.OperationId
                    Revision = view.Version
                    Command =
                        CommandKinds.all
                        |> List.find (fun kind -> CommandKinds.token kind = value.CommandName)
                    RecordedAt = value.RecordedAt
                    RecordedBy = value.RecordedBy
                }
        | HistoryDetail.Full -> HistoryEntry.FullEntry(TypedProjection.receipt value)

    let private entryVersion (entry: HistoryEntry) =
        match entry with
        | HistoryEntry.SummaryEntry item -> item.Revision
        | HistoryEntry.FullEntry item -> item.Snapshot.Version

    let private historyPage
        (request: HistoryRequest)
        (page: ClaimCore.Application.HistoryPage)
        : HistoryResultPage =
        let entries = page.Items |> List.map (historyEntry request.Detail)
        let visible = entries |> List.truncate request.Limit

        let cursor =
            if entries.Length > visible.Length then
                visible
                |> List.tryLast
                |> Option.map entryVersion
                |> Option.map HistoryCursor.encode
            else
                page.NextAfterVersion |> Option.map HistoryCursor.encode

        {
            Entries = visible
            NextCursor = cursor
        }

    let private existingHistory
        (store: IClaimStore)
        (request: HistoryRequest)
        (after: int64)
        (cancellationToken: CancellationToken)
        : Task<QueryOutcome<Lookup<HistoryResultPage, string>>> =
        task {
            let! current = store.Get request.CaseReference

            if cancellationToken.IsCancellationRequested then
                return QueryOutcome.Cancelled
            else
                match current with
                | Error failure -> return QueryOutcome.Failed(TypedProjection.coreFault failure)
                | Ok None -> return QueryOutcome.Succeeded(Lookup.NotFound request.CaseReference)
                | Ok(Some _) ->
                    let! result = store.History(request.CaseReference, after)

                    if cancellationToken.IsCancellationRequested then
                        return QueryOutcome.Cancelled
                    else
                        match result with
                        | Error failure ->
                            return QueryOutcome.Failed(TypedProjection.coreFault failure)
                        | Ok page ->
                            return QueryOutcome.Succeeded(Lookup.Found(historyPage request page))
        }

    let history
        (store: IClaimStore)
        (request: HistoryRequest)
        (cancellationToken: CancellationToken)
        : Task<QueryOutcome<Lookup<HistoryResultPage, string>>> =
        let afterVersion =
            request.AfterCursor
            |> Option.map HistoryCursor.decode
            |> Option.defaultValue (Ok 0L)

        if cancellationToken.IsCancellationRequested then
            Task.FromResult(QueryOutcome.Cancelled)
        elif request.Limit < 1 || request.Limit > limit then
            Task.FromResult(
                QueryOutcome.Rejected(invalid "limit" $"Use a value from 1 through {limit}.")
            )
        else
            match Claim.validateReference request.CaseReference, afterVersion with
            | Error rejection, _ ->
                Task.FromResult(QueryOutcome.Rejected(TypedProjection.rejection rejection))
            | _, Error message -> Task.FromResult(QueryOutcome.Rejected(invalid "cursor" message))
            | Ok(), Ok after -> existingHistory store request after cancellationToken

    let observe
        (store: IClaimStore)
        (operationId: Guid)
        (cancellationToken: CancellationToken)
        : Task<QueryOutcome<Lookup<OperationReceipt, Guid>>> =
        if cancellationToken.IsCancellationRequested then
            Task.FromResult(QueryOutcome.Cancelled)
        elif operationId = Guid.Empty then
            Task.FromResult(QueryOutcome.Rejected(invalid "operationId" "Use a non-empty UUID."))
        else
            task {
                let! result = store.Operation operationId

                match result with
                | Ok(Some value) ->
                    return QueryOutcome.Succeeded(Lookup.Found(TypedProjection.receipt value))
                | _ when cancellationToken.IsCancellationRequested -> return QueryOutcome.Cancelled
                | Ok None -> return QueryOutcome.Succeeded(Lookup.NotFound operationId)
                | Error failure -> return QueryOutcome.Failed(TypedProjection.coreFault failure)
            }
