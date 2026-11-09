namespace ClaimCore.Application

open System
open System.Security.Cryptography
open System.Text
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Shared timeline construction keeps conflicts and ordinary history under the same disclosure rules.
module internal TimelineHistory =
    let binding parent actor filter =
        [
            CaseId.format parent
            ActorId.format actor
            filter |> Option.map TimelineFilter.token |> Option.defaultValue ""
        ]
        |> String.concat "\u001f"
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexStringLower

    let subject =
        function
        | EntryTarget.Case(_, revision, act, _) -> Ok(TimelineSubject.CaseFact(revision, act))
        | EntryTarget.Note note -> Ok(TimelineSubject.Note(note.Act, note.NoteId))
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
        | EntryTarget.Authority _ -> Error CoreFault.StoreIntegrityError

    let belongs parent (filter: TimelineFilter option) (entry: StoredEntry) =
        match entry.Subject with
        | EntryTarget.Case(id, _, _, _) ->
            id = parent && (filter.IsNone || filter = Some TimelineFilter.Facts)
        | EntryTarget.Work work ->
            work.CaseId = parent && (filter.IsNone || filter = Some TimelineFilter.Work)
        | EntryTarget.Reminder reminder ->
            reminder.CaseId = parent && (filter.IsNone || filter = Some TimelineFilter.Work)
        | EntryTarget.Note note ->
            note.CaseId = parent && (filter.IsNone || filter = Some TimelineFilter.Notes)
        | EntryTarget.Authority _ -> false

    let inRange (range: ChangeRange) subject =
        let revision value =
            value > range.ExpectedRevision && value <= range.ActualRevision

        match range.Entity, subject with
        | ChangeEntity.Case, EntryTarget.Case(_, value, _, _) -> revision value
        | ChangeEntity.Work, EntryTarget.Work work -> revision work.Revision
        | ChangeEntity.Reminder id, EntryTarget.Reminder reminder ->
            (reminder.ReminderId = id && revision reminder.Revision)
            || (reminder.NextReminderId = Some id && revision Revision.first)
        | _ -> false

    let validPage parent filter range before limit (page: Page<StoredEntry, Sequence>) =
        page.Items.Length <= limit
        && (page.Items
            |> List.forall (fun entry ->
                belongs parent filter entry
                && (range |> Option.forall (fun range -> inRange range entry.Subject))
                && (before |> Option.forall (fun position -> entry.Sequence < position))))
        && (page.Items
            |> List.pairwise
            |> List.forall (fun (newer, older) -> newer.Sequence > older.Sequence))
        && (page.Next
            |> Option.forall (fun next ->
                page.Items |> List.tryLast |> Option.exists (fun entry -> entry.Sequence = next)))

    let validContent erased =
        function
        | EntryContent.ErasedCase
        | EntryContent.Case(_, None) -> erased
        | EntryContent.Case(_, Some _)
        | EntryContent.Work _
        | EntryContent.Note _
        | EntryContent.Reminder _ -> not erased
        | EntryContent.Authority _
        | EntryContent.ErasedProfile -> false

    let validate (services: ServiceContext) entries =
        entries
        |> List.map (fun entry ->
            LedgerEntries.validate services.InstallationId entry
            |> Result.map (fun value -> entry, LedgerEntries.content value))
        |> QueryResults.collect

    let entries
        (services: ServiceContext)
        (tx: IReadTx)
        erased
        full
        (range: ChangeRange option)
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

                                        TimelineDifferences.describe
                                            (match entry.Subject with
                                             | EntryTarget.Case(_, _, _, date) -> date
                                             | EntryTarget.Work value -> value.BusinessDate
                                             | EntryTarget.Reminder value -> value.BusinessDate
                                             | EntryTarget.Note value -> value.BusinessDate
                                             | EntryTarget.Authority _ ->
                                                 invalidOp
                                                     "Case history excludes authority entries.")
                                            actors
                                            content
                                            before
                                        |> Result.map (fun changes ->
                                            match range |> Option.map _.Entity with
                                            | Some(ChangeEntity.Reminder id) ->
                                                changes
                                                |> List.filter (function
                                                    | TimelineChange.Reminder difference ->
                                                        difference.ReminderId = id
                                                    | _ -> false)
                                            | _ -> changes)
                                        |> Result.bind (fun changes ->
                                            subject entry.Subject
                                            |> Result.bind (fun subject ->
                                                TimelineOverview.create
                                                    (range |> Option.map _.Entity)
                                                    subject
                                                    content
                                                    changes)
                                            |> Result.bind (fun overview ->
                                                TimelineDisclosure.create changes overview
                                                |> Result.map TimelineChanges.Disclosed
                                                |> Result.mapError (fun _ ->
                                                    CoreFault.StoreIntegrityError)))

                                match
                                    subject entry.Subject,
                                    TimelineDifferences.actor actors entry.ActorId,
                                    changes
                                with
                                | Ok subject, Ok author, Ok changes ->
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
