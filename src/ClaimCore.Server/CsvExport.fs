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
        Chunks: seq<byte array>
        ItemCount: int
        NextCursor: string option
    }

[<NoEquality; NoComparison>]
type internal ExportTraversal =
    {
        Header: byte array
        Read: string option -> CancellationToken -> Task<Result<ExportPage, ExportFailure>>
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

                while not finished && failure.IsNone do
                    token.ThrowIfCancellationRequested()
                    let! page = traversal.Read cursor token
                    token.ThrowIfCancellationRequested()

                    match page with
                    | Error error -> failure <- Some error
                    | Ok page ->
                        let before = bytes.Length

                        let fits =
                            page.Chunks
                            |> Seq.forall (fun chunk ->
                                token.ThrowIfCancellationRequested()
                                append chunk)

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
                    | None -> Ok(bytes.ToArray())
        }

    let private page encode count next outcome =
        match outcome with
        | Outcome.Succeeded value ->
            Ok
                {
                    Chunks = encode value
                    ItemCount = count value
                    NextCursor = next value
                }
        | Outcome.Rejected refusal -> Error(ExportFailure.Rejected(HumanReplies.rejection refusal))
        | Outcome.Failed fault -> Error(ExportFailure.Failed fault)

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
                                Service.timeline
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
                                    _.NextCursor
                                    result
                        }
            }
