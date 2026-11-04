namespace ClaimCore.Application

open System
open System.Security.Cryptography
open System.Text
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// One snapshot owns current history disclosure, predecessor values and current actor names.
module internal TimelineQueries =
    let private binding parent actor filter =
        [
            CaseId.format parent
            ActorId.format actor
            filter |> Option.map TimelineFilter.token |> Option.defaultValue ""
        ]
        |> String.concat "\u001f"
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexStringLower

    let private subject =
        function
        | EntryTarget.Case(_, revision, act, _) -> Ok(TimelineSubject.CaseFact(revision, act))
        | EntryTarget.Work work -> Ok(TimelineSubject.Work(work.Revision, work.Act))
        | EntryTarget.Reminder reminder ->
            Ok(
                TimelineSubject.Reminder(
                    reminder.Act,
                    reminder.ReminderId,
                    reminder.Revision,
                    reminder.NextReminderId |> Option.map (fun id -> id, Revision.first)
                )
            )
        | EntryTarget.Authority _
        | EntryTarget.NonAcceptance _ -> Error CoreFault.StoreIntegrityError

    let private belongs parent filter (entry: StoredEntry) =
        match entry.Subject with
        | EntryTarget.Case(id, _, _, _) -> id = parent && filter <> Some TimelineFilter.Work
        | EntryTarget.Work work -> work.CaseId = parent && filter <> Some TimelineFilter.Facts
        | EntryTarget.Reminder reminder ->
            reminder.CaseId = parent && filter <> Some TimelineFilter.Facts
        | EntryTarget.Authority _
        | EntryTarget.NonAcceptance _ -> false

    let private validPage parent filter before limit (page: Page<StoredEntry, Sequence>) =
        page.Items.Length <= limit
        && (page.Items
            |> List.forall (fun entry ->
                belongs parent filter entry
                && (before |> Option.forall (fun position -> entry.Sequence < position))))
        && (page.Items
            |> List.pairwise
            |> List.forall (fun (newer, older) -> newer.Sequence > older.Sequence))
        && (page.Next
            |> Option.forall (fun next ->
                page.Items |> List.tryLast |> Option.exists (fun entry -> entry.Sequence = next)))

    let private validContent erased =
        function
        | EntryContent.ErasedCase
        | EntryContent.Case(_, None) -> erased
        | EntryContent.Case(_, Some _)
        | EntryContent.Work _
        | EntryContent.Reminder _ -> not erased
        | EntryContent.NonAcceptance
        | EntryContent.Authority _
        | EntryContent.ErasedProfile -> false

    let private validate (services: ServiceContext) entries =
        entries
        |> List.map (fun entry ->
            LedgerEntries.validate services.InstallationId entry
            |> Result.map (fun value -> entry, LedgerEntries.content value))
        |> QueryResults.collect

    let private entries
        (services: ServiceContext)
        (tx: IReadTx)
        erased
        full
        (page: Page<StoredEntry, Sequence>)
        =
        task {
            match validate services page.Items with
            | Error fault -> return Error fault
            | Ok values when values |> List.exists (snd >> validContent erased >> not) ->
                return Error CoreFault.StoreIntegrityError
            | Ok values ->
                let! previous =
                    if full && not erased then
                        tx.PreviousEntries page.Items
                    else
                        System.Threading.Tasks.Task.FromResult Map.empty

                let sequences = page.Items |> List.map _.Sequence |> Set.ofList

                if
                    previous.Count > page.Items.Length
                    || (previous
                        |> Map.exists (fun sequence entry ->
                            not (Set.contains sequence sequences) || entry.Sequence >= sequence))
                then
                    return Error CoreFault.StoreIntegrityError
                else
                    match validate services (previous |> Map.values |> Seq.toList) with
                    | Error fault -> return Error fault
                    | Ok prior ->
                        let contents =
                            prior
                            |> List.map (fun (entry, content) -> entry.Sequence, content)
                            |> Map.ofList

                        let authors = page.Items |> List.map _.ActorId

                        let responsibility =
                            if full && not erased then
                                (values @ prior)
                                |> List.collect (snd >> TimelineDifferences.actorIds)
                            else
                                []

                        let! actors = tx.ActorRefs(authors @ responsibility |> List.distinct)

                        return
                            values
                            |> List.map (fun (entry, content) ->
                                let changes =
                                    if erased then
                                        Ok TimelineChanges.Erased
                                    elif not full then
                                        Ok TimelineChanges.Withheld
                                    else
                                        let before =
                                            previous
                                            |> Map.tryFind entry.Sequence
                                            |> Option.bind (fun prior ->
                                                Map.tryFind prior.Sequence contents)

                                        TimelineDifferences.describe actors content before
                                        |> Result.map TimelineChanges.Disclosed

                                match
                                    subject entry.Subject,
                                    Map.tryFind entry.ActorId actors,
                                    changes
                                with
                                | Ok subject, Some author, Ok changes ->
                                    Ok
                                        {
                                            CaseTimelineEntry.OperationId = entry.OperationId
                                            Position =
                                                LedgerEntries.position
                                                    entry.Sequence
                                                    entry.EntryHash
                                            Subject = subject
                                            RecordedAt = entry.RecordedAt
                                            RecordedBy = author
                                            Changes = changes
                                        }
                                | Error fault, _, _
                                | _, _, Error fault -> Error fault
                                | _ -> Error CoreFault.StoreIntegrityError)
                            |> QueryResults.collect
        }

    let read (services: ServiceContext) (request: CaseTimelineRequest) token =
        task {
            if not (QueryResults.validLimit request.Limit) then
                return QueryResults.invalidLimit ()
            else
                match CaseExecution.validateSelector request.Target with
                | Error refusal -> return Outcome.Rejected refusal
                | Ok() ->
                    let! result =
                        services.Store.Read
                            {
                                CancellationToken = token
                                Work =
                                    fun tx ->
                                        task {
                                            let! view =
                                                ExecutionFacts.case services tx None request.Target

                                            match view.Case with
                                            | Some parent when
                                                Authority.can
                                                    services.Principal
                                                    view.Actor
                                                    Capability.ReadHistory
                                                    (Scope.Case parent.CaseId)
                                                ->
                                                let scope =
                                                    binding
                                                        parent.CaseId
                                                        view.Actor.Actor.Value.ActorId
                                                        request.Filter

                                                match
                                                    QueryResults.cursor
                                                        (Cursors.decodeTimeline
                                                            services.InstallationId
                                                            scope)
                                                        request.AfterCursor
                                                with
                                                | Error refusal -> return Outcome.Rejected refusal
                                                | Ok before ->
                                                    let! page =
                                                        tx.TimelinePage
                                                            {
                                                                CaseId = parent.CaseId
                                                                Filter = request.Filter
                                                                BeforeSequence = before
                                                                Limit = request.Limit
                                                            }

                                                    if
                                                        not (
                                                            validPage
                                                                parent.CaseId
                                                                request.Filter
                                                                before
                                                                request.Limit
                                                                page
                                                        )
                                                    then
                                                        return
                                                            Outcome.Failed
                                                                CoreFault.StoreIntegrityError
                                                    else
                                                        let erased =
                                                            CaseState.lifecycle
                                                                parent.Snapshot.State
                                                                =
                                                                Lifecycle.Erased

                                                        let full =
                                                            Authority.can
                                                                services.Principal
                                                                view.Actor
                                                                Capability.ReadHistoryFull
                                                                (Scope.Case parent.CaseId)

                                                        let! items =
                                                            entries services tx erased full page

                                                        return
                                                            items
                                                            |> Result.map (fun items ->
                                                                {
                                                                    CaseTimelinePage.Basis =
                                                                        CaseWorkReads.basis
                                                                            services
                                                                            tx.ObservedAt
                                                                    CaseId = parent.CaseId
                                                                    Reference =
                                                                        parent.Snapshot.Reference
                                                                    Items = items
                                                                    NextCursor =
                                                                        page.Next
                                                                        |> Option.map (
                                                                            Cursors.encodeTimeline
                                                                                services
                                                                                    .InstallationId
                                                                                scope
                                                                        )
                                                                })
                                                            |> Result.map Outcome.Succeeded
                                                            |> Result.defaultWith Outcome.Failed
                                            | _ -> return QueryResults.unavailable
                                        }
                            }

                    return QueryResults.readResult result
        }
