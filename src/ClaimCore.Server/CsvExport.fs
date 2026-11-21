namespace ClaimCore.Server

open System
open System.IO
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Wire.Export

[<NoEquality; NoComparison>]
type internal ExportPage =
    {
        Rows: seq<byte array>
        ItemCount: int
        CaseId: CaseId option
        NextCursor: string option
    }

[<NoEquality; NoComparison>]
type internal ExportTraversal =
    {
        Header: byte array
        Read: string option -> CancellationToken -> Task<Result<ExportPage, ExportFailure>>
    }

type internal MaterializedExport =
    {
        Bytes: byte array
        RowCount: int
        CaseId: CaseId option
    }

/// Materialize the whole authorized traversal before any success bytes or headers are sent.
module internal CsvExport =
    let private limit = ExportFailure.LimitExceeded ExportAdvice.limitExceeded

    let private continuation =
        ExportFailure.InvalidContinuation ExportAdvice.invalidContinuation

    let materialize maximum (token: CancellationToken) traversal =
        task {
            use bytes = new MemoryStream(min maximum 8192)

            let append (chunk: byte array) =
                if chunk.Length > maximum - int bytes.Length then
                    false
                else
                    bytes.Write(chunk)
                    true

            if not (append traversal.Header) then
                return Error limit
            else
                let seen = HashSet<string>(StringComparer.Ordinal)
                let mutable cursor = None
                let mutable finished = false
                let mutable failure = None
                let mutable rowCount = 0
                let mutable caseId = None

                while not finished && failure.IsNone do
                    token.ThrowIfCancellationRequested()
                    let! page = traversal.Read cursor token
                    token.ThrowIfCancellationRequested()

                    match page with
                    | Error error -> failure <- Some error
                    | Ok page ->
                        let before = bytes.Length
                        caseId <- caseId |> Option.orElse page.CaseId

                        let fits =
                            page.Rows
                            |> Seq.forall (fun row ->
                                token.ThrowIfCancellationRequested()
                                let appended = append row

                                if appended then
                                    rowCount <- rowCount + 1

                                appended)

                        if not fits then
                            failure <- Some limit
                        else
                            match page.NextCursor with
                            | None -> finished <- true
                            | Some next when
                                page.ItemCount <= 0
                                || bytes.Length = before
                                || Some next = cursor
                                || not (seen.Add next)
                                ->
                                failure <- Some continuation
                            | Some next -> cursor <- Some next

                return
                    match failure with
                    | Some failure -> Error failure
                    | None ->
                        Ok
                            {
                                Bytes = bytes.ToArray()
                                RowCount = rowCount
                                CaseId = caseId
                            }
        }

    let private page encode count caseId next outcome =
        match outcome with
        | Outcome.Succeeded value ->
            Ok
                {
                    Rows = encode value
                    ItemCount = count value
                    CaseId = caseId value
                    NextCursor = next value
                }
        | Outcome.Rejected refusal -> Error(ExportFailure.Rejected(HumanReplies.rejection refusal))
        | Outcome.Failed fault -> Error(ExportFailure.Failed fault)

    let private noCase _ = None

    let private english =
        lazy
            (let entries, _ = LocalizationPolicy.load "en" [ CatalogRoutes.core "en" ] []
             entries |> Map.toList |> Messages.catalog |> Result.defaultWith invalidOp)

    let private render key args = Messages.render english.Value key args

    /// Each read opens its own authorized core query transaction. No HTTP admission is reacquired.
    let traversal services =
        function
        | ExportSelection.Cases filter ->
            {
                Header = CaseCsv.csvHeader
                Read =
                    fun cursor token ->
                        task {
                            let! result =
                                Service.list
                                    services
                                    {
                                        CaseListRequest.Filter = filter
                                        Page =
                                            {
                                                AfterCursor = cursor
                                                Limit = Paging.MaximumPageSize
                                            }
                                    }
                                    token

                            return
                                page
                                    (fun (value: CaseSummaryPage) ->
                                        CaseCsv.csvChunks value.Basis value.Items)
                                    (fun value -> value.Items.Length)
                                    noCase
                                    _.NextCursor
                                    result
                        }
            }
        | ExportSelection.CaseSummary selection ->
            {
                Header = CaseSummaryCsv.header
                Read =
                    fun cursor token ->
                        task {
                            let! result =
                                Service.caseSummary
                                    services
                                    {
                                        Selection = selection
                                        Page =
                                            {
                                                AfterCursor = cursor
                                                Limit = Paging.MaximumPageSize
                                            }
                                    }
                                    token

                            return
                                page
                                    CaseSummaryCsv.chunks
                                    (fun result -> result.Rows.Length)
                                    noCase
                                    _.NextCursor
                                    result
                        }
            }
        | ExportSelection.Timeline selection ->
            {
                Header = TimelineCsv.csvHeader
                Read =
                    fun cursor token ->
                        task {
                            let! result =
                                Service.timelineForExport
                                    services
                                    { selection with
                                        AfterCursor = cursor
                                        Limit = Paging.MaximumPageSize
                                    }
                                    token

                            return
                                page
                                    TimelineCsv.csvChunks
                                    (fun (value: CaseTimelinePage) -> value.Items.Length)
                                    (fun value -> Some value.CaseId)
                                    _.NextCursor
                                    result
                        }
            }
        | ExportSelection.Reminders selection ->
            {
                Header = ReminderCsv.csvHeader
                Read =
                    fun cursor token ->
                        task {
                            let! result =
                                Service.reminders
                                    services
                                    { selection with
                                        AfterCursor = cursor
                                        Limit = Paging.MaximumPageSize
                                    }
                                    token

                            return
                                page
                                    (fun (value: ReminderPage) ->
                                        ReminderCsv.csvChunks render value.Items)
                                    (fun value -> value.Items.Length)
                                    (fun value ->
                                        match selection.Target with
                                        | None -> None
                                        | Some(CaseSelector.Id id) -> Some id
                                        | Some(CaseSelector.Reference _) ->
                                            value.Items
                                            |> List.tryHead
                                            |> Option.map (fun row -> Reminder.caseId row.Reminder))
                                    _.NextCursor
                                    result
                        }
            }
        | ExportSelection.PeopleHistory selection ->
            {
                Header = PeopleHistoryCsv.csvHeader
                Read =
                    fun cursor token ->
                        task {
                            let! result =
                                Service.peopleHistory
                                    services
                                    { selection with
                                        AfterCursor = cursor
                                        Limit = Paging.MaximumPageSize
                                    }
                                    token

                            return
                                page
                                    (fun (value: PeopleHistoryPage) ->
                                        PeopleHistoryCsv.csvChunks render value.Entries)
                                    (fun value -> value.Entries.Length)
                                    noCase
                                    _.NextCursor
                                    result
                        }
            }
