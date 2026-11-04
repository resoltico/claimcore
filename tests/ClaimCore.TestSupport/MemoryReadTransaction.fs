module internal ClaimCore.TestSupport.MemoryReadTransaction

open ClaimCore.RecordFormat
open System
open System.Threading
open System.Threading.Tasks
open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application.Contracts

open ClaimCore.TestSupport.MemoryStoreState

let private selected (state: State) (selection: CaseSelection) (record: StoredCase) =
    let scopedRead =
        state.Grants
        |> Seq.exists (fun (id, role, scope) ->
            id = selection.ActorId
            && scope = Some record.CaseId
            && List.contains role selection.ReadRoles)

    let forbidden =
        state.Grants
        |> Seq.exists (fun (id, role, scope) ->
            id = selection.ActorId
            && scope = Some record.CaseId
            && List.contains role selection.ForbiddenRoles)

    (selection.Visibility = CaseVisibility.Every || scopedRead)
    && (selection.Filter.View
        |> Option.forall (fun view ->
            CaseView.matches
                selection.DateBasis.BusinessDate
                selection.ActorId
                view
                {
                    State =
                        record.Snapshot.State
                        |> CaseState.map (fun fields ->
                            fields.Status, CaseProgress.ofPayment fields.Progress)
                    Responsible =
                        record.Index.Responsible
                        |> Option.map (fun actor ->
                            actor,
                            (assignedAccount state record.CaseId actor).Availability =
                                ActorAvailability.Available)
                    NextPendingDue = record.Index.FollowUp
                }))
    && (selection.Filter.Assignment
        |> Option.forall (function
            | CaseAssignment.Mine -> record.Index.Responsible = Some selection.ActorId
            | CaseAssignment.Unassigned ->
                record.Index.Responsible.IsNone
                && CaseState.lifecycle record.Snapshot.State <> Lifecycle.Erased))
    && (selection.Filter.Due
        |> Option.forall (fun due ->
            CaseDue.at
                selection.DateBasis.BusinessDate
                (CaseState.lifecycle record.Snapshot.State)
                record.Index.Status
                record.Index.FollowUp
                =
                due))
    && (selection.Filter.ResponsibleActor
        |> Option.forall (fun actor -> record.Index.Responsible = Some actor))
    && not forbidden
    && CaseListFilter.matches
        selection.Filter
        (CaseState.lifecycle record.Snapshot.State)
        record.Index.Status
        record.Index.Progress
        (record.Index.SearchText
         |> Option.map (fun text ->
             match record.Index.WorkSearchText with
             | None -> text
             | Some summary -> text + "\u001f" + summary))


let private viewFacts state (record: StoredCase) : CaseWorkFacts =
    {
        State =
            record.Snapshot.State
            |> CaseState.map (fun fields -> fields.Status, CaseProgress.ofPayment fields.Progress)
        Responsible =
            record.Index.Responsible
            |> Option.map (fun actor ->
                actor,
                (assignedAccount state record.CaseId actor).Availability =
                    ActorAvailability.Available)
        NextPendingDue = record.Index.FollowUp
    }

let private total money =
    money
    |> Option.map (fun value ->
        {
            CurrencyTotal.Currency = Money.currencyValue value
            Amount =
                DecimalTotal.parse (Money.format value)
                |> Result.defaultWith (string >> failwith)
        })

let private compareDates (left: CalendarDate option) right =
    match left, right with
    | None, None -> 0
    | None, Some _ -> 1
    | Some _, None -> -1
    | Some left, Some right -> compare left right

let private dateAfter (found: CalendarDate option) boundary referenceAfter =
    match found, boundary with
    | None, Some _ -> true
    | Some _, None -> false
    | None, None -> referenceAfter
    | Some found, Some boundary -> found > boundary || (found = boundary && referenceAfter)

let private workSummary state today id =
    state.Work
    |> Map.tryFind id
    |> Option.map (fun work ->
        let pending = pendingReminders state id

        {
            CaseWorkSummary.Work = work
            Next = List.tryHead pending
            Pending = int64 pending.Length
            Overdue =
                pending
                |> List.filter (fun reminder -> CalendarDate.value (Reminder.due reminder) < today)
                |> List.length
                |> int64
        })

let private authorityView state principal caseId =
    let actor =
        state.Actors
        |> Map.values
        |> Seq.tryFind (fun actor -> actor.Principal = principal)

    let view =
        actor
        |> Option.map (fun actor ->
            {
                ActorAuthority.Actor = actor
                Grants =
                    state.Grants
                    |> Seq.choose (fun (id, role, scope) ->
                        if id = actor.ActorId && (scope.IsNone || scope = caseId) then
                            Some
                                {
                                    ActorGrant.Role = role
                                    Scope =
                                        scope
                                        |> Option.map Scope.Case
                                        |> Option.defaultValue Scope.Installation
                                }
                        else
                            None)
                    |> Seq.toList
            })
        |> AuthorityView.ofActor

    let assigned =
        actor
        |> Option.exists (fun actor ->
            state.Grants
            |> Seq.exists (fun (id, role, scope) ->
                id = actor.ActorId
                && scope.IsSome
                && List.contains role (Authority.readRoles principal)
                && not (
                    state.Grants
                    |> Seq.exists (fun (badId, badRole, badScope) ->
                        badId = id
                        && badScope = scope
                        && List.contains badRole (Authority.invalidRoles principal))
                )))

    { view with
        HasScopedCases = caseId.IsNone && assigned
    }


type internal Reader
    (
        get: unit -> State,
        _installationId,
        observed,
        read: unit -> unit,
        ?factRead: AuthorityFact -> unit
    ) =
    let factRead = defaultArg factRead ignore

    interface IReadTx with
        member _.ObservedAt = observed
        member _.Maintenance() = Task.FromResult((get ()).Maintenance)

        member _.Authority
            {
                AuthorityQuery.Principal = principal
                CaseId = caseId
            }
            =
            read ()
            Task.FromResult(authorityView (get ()) principal caseId)

        member _.GlobalReadFacts principal =
            read ()
            let state = get ()

            Task.FromResult
                {
                    GlobalReadFacts.Authority = authorityView state principal None
                    Maintenance = state.Maintenance
                }

        member _.AuthorityCases(principal, caseIds) =
            read ()
            let state = get ()

            let actor =
                state.Actors
                |> Map.values
                |> Seq.tryFind (fun actor -> actor.Principal = principal)

            let view =
                actor
                |> Option.map (fun actor ->
                    {
                        ActorAuthority.Actor = actor
                        Grants =
                            state.Grants
                            |> Seq.choose (fun (id, role, scope) ->
                                if
                                    id = actor.ActorId
                                    && (scope.IsNone
                                        || (scope
                                            |> Option.exists (fun id -> List.contains id caseIds)))
                                then
                                    Some
                                        {
                                            ActorGrant.Role = role
                                            Scope =
                                                scope
                                                |> Option.map Scope.Case
                                                |> Option.defaultValue Scope.Installation
                                        }
                                else
                                    None)
                            |> Seq.toList
                    })
                |> AuthorityView.ofActor

            Task.FromResult view

        member _.FindCase id =
            read ()
            Task.FromResult(Map.tryFind id (get ()).Cases |> Option.map (readCase (get ()).Entries))

        member _.FindCaseByReference reference =
            read ()

            Task.FromResult(
                (get ()).Cases
                |> Map.values
                |> Seq.tryFind (fun record ->
                    CaseReference.identity record.Snapshot.Reference = CaseReference.key reference)
                |> Option.map (readCase (get ()).Entries)
            )

        member _.FindWork id =
            read ()
            Task.FromResult(Map.tryFind id (get ()).Work)

        member _.FindReminder id =
            read ()
            Task.FromResult(Map.tryFind id (get ()).Reminders)

        member _.WorkSummaries(ids, today) =
            read ()
            let state = get ()

            let rows =
                ids
                |> List.choose (fun id ->
                    workSummary state today id |> Option.map (fun work -> id, work))
                |> Map.ofList

            Task.FromResult rows

        member _.AssignedAccounts pairs =
            read ()
            let state = get ()

            Task.FromResult(
                pairs
                |> List.map (fun (caseId, actorId) -> caseId, assignedAccount state caseId actorId)
                |> Map.ofList
            )

        member _.ResponsibilityCounts(selection, actors) =
            read ()

            if actors.Length > Paging.MaximumPageSize then
                invalidArg (nameof actors) "Use at most one admitted actor page."

            let state = get ()

            let assigned =
                state.Cases
                |> Map.values
                |> Seq.filter (selected state selection)
                |> Seq.choose _.Index.Responsible
                |> Seq.countBy id
                |> Map.ofSeq

            actors
            |> List.distinct
            |> List.map (fun actor ->
                actor, (Map.tryFind actor assigned |> Option.defaultValue 0 |> int64))
            |> Map.ofList
            |> Task.FromResult

        member _.ReminderPage query =
            read ()
            let state = get ()

            let rows =
                state.Reminders
                |> Map.values
                |> Seq.choose (fun row -> row.Snapshot |> Option.map (fun value -> row, value))
                |> Seq.filter (fun (row, value) ->
                    let parent = state.Cases[row.CaseId]

                    let actionable =
                        CaseState.lifecycle parent.Snapshot.State = Lifecycle.Active
                        && parent.Index.Status = Some CaseStatus.Opened

                    selected state query.Selection parent
                    && (query.TargetCaseId |> Option.forall ((=) row.CaseId))
                    && (query.State |> Option.forall ((=) (Reminder.state value)))
                    && (query.View
                        |> Option.forall (fun view ->
                            ReminderView.matches
                                query.Selection.DateBasis.BusinessDate
                                query.Selection.ActorId
                                view
                                (viewFacts state parent)
                                value))
                    && (query.IncludeInactive || actionable)
                    && (query.Text
                        |> Option.forall (fun text ->
                            (CaseSearch.normalize (ReminderAction.format (Reminder.action value)))
                                .Contains(CaseSearchText.value text, StringComparison.Ordinal)))
                    && (query.Due
                        |> Option.forall (fun due ->
                            let date = CalendarDate.value (Reminder.due value)

                            match due with
                            | ReminderDue.Overdue -> date < query.Selection.DateBasis.BusinessDate
                            | ReminderDue.Today -> date = query.Selection.DateBasis.BusinessDate
                            | ReminderDue.Upcoming ->
                                date > query.Selection.DateBasis.BusinessDate))
                    && (query.After
                        |> Option.forall (fun after ->
                            Reminder.due value > after.Due
                            || (Reminder.due value = after.Due
                                && compareId
                                    ReminderId.format
                                    (Reminder.id value)
                                    after.ReminderId
                                    >
                                    0))))
                |> Seq.sortWith (fun (_, left) (_, right) ->
                    let date = compare (Reminder.due left) (Reminder.due right)

                    if date <> 0 then
                        date
                    else
                        compareId ReminderId.format (Reminder.id left) (Reminder.id right))
                |> Seq.map (fun (row, _) ->
                    let parent = state.Cases[row.CaseId]

                    {
                        ReminderRow.Stored = row
                        Reference = parent.Snapshot.Reference
                        Lifecycle = CaseState.lifecycle parent.Snapshot.State
                        Status = parent.Index.Status
                    })
                |> Seq.toList

            Task.FromResult(
                page
                    query.Limit
                    (fun row ->
                        {
                            ReminderPosition.Due =
                                row.Stored.Snapshot |> Option.get |> Reminder.due
                            ReminderId = row.Stored.ReminderId
                        })
                    rows
            )

        member _.CaseViewCounts selection =
            read ()
            let state = get ()

            let rows =
                state.Cases
                |> Map.values
                |> Seq.filter (selected state selection)
                |> Seq.map (viewFacts state)
                |> Seq.toList

            CaseView.all
            |> List.map (fun view ->
                {
                    CaseViewCount.View = view
                    Count =
                        rows
                        |> List.filter (
                            CaseView.matches
                                selection.DateBasis.BusinessDate
                                selection.ActorId
                                view
                        )
                        |> List.length
                        |> int64
                })
            |> Task.FromResult

        member _.ReminderViewCounts selection =
            read ()
            let state = get ()

            let rows =
                state.Reminders
                |> Map.values
                |> Seq.choose (fun row ->
                    match Map.tryFind row.CaseId state.Cases, row.Snapshot with
                    | Some parent, Some reminder when selected state selection parent ->
                        Some(viewFacts state parent, reminder)
                    | _ -> None)
                |> Seq.toList

            ReminderView.all
            |> List.map (fun view ->
                {
                    ReminderViewCount.View = view
                    Count =
                        rows
                        |> List.filter (fun (parent, reminder) ->
                            ReminderView.matches
                                selection.DateBasis.BusinessDate
                                selection.ActorId
                                view
                                parent
                                reminder)
                        |> List.length
                        |> int64
                })
            |> Task.FromResult

        member _.WorkOverview(selection, mine) =
            read ()
            let state = get ()

            let scope =
                { selection with
                    Filter =
                        { CaseListFilter.empty<CaseSearchText> with
                            Assignment = if mine then Some CaseAssignment.Mine else None
                        }
                }

            let rows =
                state.Cases
                |> Map.values
                |> Seq.filter (selected state scope)
                |> Seq.filter (fun parent ->
                    CaseState.lifecycle parent.Snapshot.State = Lifecycle.Active
                    && parent.Index.Status = Some CaseStatus.Opened)
                |> Seq.toList

            let attention =
                rows
                |> List.map (fun parent ->
                    let pending = pendingReminders state parent.CaseId

                    let overdue =
                        pending
                        |> List.filter (fun reminder ->
                            CalendarDate.value (Reminder.due reminder) <
                                selection.DateBasis.BusinessDate)
                        |> List.length

                    let unassigned = parent.Index.Responsible.IsNone

                    let unavailable =
                        parent.Index.Responsible
                        |> Option.exists (fun actor ->
                            (assignedAccount state parent.CaseId actor).Availability
                            <> ActorAvailability.Available)

                    pending.IsEmpty, overdue, unassigned, unavailable)

            let count predicate =
                attention |> List.filter predicate |> List.length |> int64

            Task.FromResult
                {
                    WorkOverview.Basis = selection.DateBasis
                    Mine = mine
                    ActionableCases = int64 rows.Length
                    MissingPlanCases = count (fun (missing, _, _, _) -> missing)
                    OverdueCases = count (fun (_, overdue, _, _) -> overdue > 0)
                    OverdueReminders =
                        attention |> List.sumBy (fun (_, overdue, _, _) -> int64 overdue)
                    UnassignedCases = count (fun (_, _, unassigned, _) -> unassigned)
                    UnavailableAssigneeCases = count (fun (_, _, _, unavailable) -> unavailable)
                    AttentionCases =
                        rows
                        |> List.filter (fun parent ->
                            CaseView.matches
                                selection.DateBasis.BusinessDate
                                selection.ActorId
                                CaseView.NeedsAttention
                                (viewFacts state parent))
                        |> List.length
                        |> int64
                }

        member _.ActivityPage query =
            read ()

            let rows =
                (get ()).Entries
                |> Map.values
                |> Seq.filter (fun entry ->
                    let parent =
                        match entry.Subject with
                        | EntryTarget.Case(id, _, _, _) -> Some id
                        | EntryTarget.Work work -> Some work.CaseId
                        | EntryTarget.Reminder reminder -> Some reminder.CaseId
                        | _ -> None

                    parent = Some query.CaseId
                    && (query.BeforeSequence
                        |> Option.forall (fun before -> entry.Sequence < before)))
                |> Seq.sortByDescending _.Sequence
                |> Seq.map copyEntry
                |> Seq.toList

            Task.FromResult(page query.Limit _.Sequence rows)

        member _.CaseStageFacts caseId =
            read ()

            let entries =
                (get ()).Entries
                |> Map.values
                |> Seq.choose (fun entry ->
                    match entry.Subject with
                    | EntryTarget.Case(id, revision, act, date) when id = caseId ->
                        Some(revision, act, date)
                    | _ -> None)
                |> Seq.sortByDescending (fun (revision, _, _) -> revision)
                |> Seq.toList

            let opened =
                entries
                |> List.tryFind (fun (revision, act, _) ->
                    revision = Revision.first && act = CaseActKind.Open)

            let closed = entries |> List.tryFind (fun (_, act, _) -> act = CaseActKind.Close)

            match entries, opened with
            | (revision, act, _) :: _, Some(_, _, opened) ->
                Some
                    {
                        CaseStageFacts.Revision = revision
                        LastAct = act
                        OpenedOn = CalendarDate.ofDate opened
                        ClosedOn =
                            closed |> Option.map (fun (_, _, date) -> CalendarDate.ofDate date)
                    }
            | _ -> None
            |> Task.FromResult

        member _.TimelinePage(query: TimelinePageQuery) =
            read ()

            let rows =
                (get ()).Entries
                |> Map.values
                |> Seq.filter (fun entry ->
                    let parent, facts =
                        match entry.Subject with
                        | EntryTarget.Case(id, _, _, _) -> Some id, true
                        | EntryTarget.Work work -> Some work.CaseId, false
                        | EntryTarget.Reminder reminder -> Some reminder.CaseId, false
                        | _ -> None, false

                    parent = Some query.CaseId
                    && (query.Range
                        |> Option.forall (fun range -> TimelineHistory.inRange range entry.Subject))
                    && (query.BeforeSequence
                        |> Option.forall (fun before -> entry.Sequence < before))
                    && (query.Filter
                        |> Option.forall (function
                            | TimelineFilter.Facts -> facts
                            | TimelineFilter.Work -> not facts)))
                |> Seq.sortByDescending _.Sequence
                |> Seq.map copyEntry
                |> Seq.toList

            Task.FromResult(page query.Limit _.Sequence rows)

        member _.PreviousEntries entries =
            read ()

            if entries.Length > Paging.MaximumPageSize then
                invalidArg (nameof entries) "Use at most one admitted timeline page."

            let sameEntity subject candidate =
                match subject, candidate with
                | EntryTarget.Case(id, revision, _, _), EntryTarget.Case(other, prior, _, _) ->
                    id = other && Revision.value prior + 1L = Revision.value revision
                | EntryTarget.Work work, EntryTarget.Work other ->
                    work.CaseId = other.CaseId
                    && Revision.value other.Revision + 1L = Revision.value work.Revision
                | EntryTarget.Reminder reminder, EntryTarget.Reminder other ->
                    (reminder.ReminderId = other.ReminderId
                     && Revision.value other.Revision + 1L = Revision.value reminder.Revision)
                    || (Revision.value reminder.Revision = 2L
                        && other.NextReminderId = Some reminder.ReminderId)
                | _ -> false

            entries
            |> List.choose (fun entry ->
                (get ()).Entries
                |> Map.values
                |> Seq.filter (fun previous ->
                    previous.Sequence < entry.Sequence
                    && sameEntity entry.Subject previous.Subject)
                |> Seq.toList
                |> function
                    | [] -> None
                    | [ previous ] -> Some previous
                    | _ -> raise CorruptProjection
                |> Option.map (fun previous -> entry.Sequence, copyEntry previous))
            |> Map.ofList
            |> Task.FromResult

        member _.FindOperation id =
            read ()

            let entries =
                (get ()).Entries
                |> Map.values
                |> Seq.filter (fun entry -> entry.OperationId = id)
                |> Seq.sortBy (fun entry -> LedgerEffects.index entry.Effects)
                |> Seq.map copyEntry
                |> Seq.toList

            let operation =
                match entries with
                | [] -> None
                | entries ->
                    StoredOperation.create entries
                    |> Result.map Some
                    |> Result.defaultWith (fun _ -> raise CorruptProjection)

            Task.FromResult operation

        member _.NonAcceptance(key: FenceKey) =
            read ()

            let controls =
                (get ()).Controls
                |> Map.values
                |> Seq.filter (fun entry ->
                    entry.Entry.ActorId = key.ActorId && entry.Entry.OperationId = key.OperationId)
                |> Seq.toList

            let control =
                match controls with
                | [] -> None
                | [ entry ] ->
                    Some
                        { entry with
                            Entry = copyEntry entry.Entry
                        }
                | _ -> raise CorruptProjection

            Task.FromResult
                {
                    NonAcceptanceEvidence.Control = control
                    ProjectedAt = Map.tryFind (key.ActorId, key.OperationId) (get ()).Fences
                }

        member _.AssigneePage query =
            read ()
            let state = get ()

            let permitted roles id =
                state.Grants
                |> Seq.exists (fun (actor, role, caseId) ->
                    actor = id
                    && (caseId.IsNone || caseId = Some query.CaseId)
                    && List.contains role roles)

            let rows =
                state.Actors
                |> Map.values
                |> Seq.filter (fun actor ->
                    actor.Enabled
                    && PrincipalKey.isHuman actor.Principal
                    && (query.AfterActorId |> Option.forall (fun after -> actor.ActorId > after))
                    && permitted query.ReadRoles actor.ActorId
                    && permitted query.WorkRoles actor.ActorId)
                |> Seq.sortBy _.ActorId
                |> Seq.truncate (query.Limit + 1)
                |> Seq.map Actor.currentReference
                |> Seq.toList

            Task.FromResult(page query.Limit _.ActorId rows)

        member _.ActorRefs ids =
            read ()
            let state = get ()

            ids
            |> List.distinct
            |> List.choose (fun id ->
                Map.tryFind id state.Actors
                |> Option.map (fun actor -> id, Actor.currentReference actor))
            |> Map.ofList
            |> Task.FromResult

        member _.FindActor id =
            read ()
            Task.FromResult(Map.tryFind id (get ()).Actors)

        member _.ActorCount() =
            read ()
            factRead AuthorityFact.ActorCount
            Task.FromResult((get ()).Actors.Count)

        member _.EnabledHumanOwners() =
            read ()
            factRead AuthorityFact.EnabledHumanOwners
            let state = get ()

            Task.FromResult(
                state.Actors
                |> Map.values
                |> Seq.filter (fun actor ->
                    actor.Enabled
                    && PrincipalKey.isHuman actor.Principal
                    && Set.contains (actor.ActorId, Role.Owner, None) state.Grants)
                |> Seq.length
            )

        member _.CasePage(query: CasePageQuery) =
            read ()
            let state = get ()

            let reference (record: StoredCase) =
                CaseReference.identity record.Snapshot.Reference

            let compareRows (left: StoredCase) (right: StoredCase) =
                let time =
                    match query.Filter.Order with
                    | CaseListOrder.Reference -> 0
                    | CaseListOrder.Recent -> compare right.Index.ChangedAt left.Index.ChangedAt
                    | CaseListOrder.Notified ->
                        compareDates left.Index.NotifiedOn right.Index.NotifiedOn
                    | CaseListOrder.FollowUp ->
                        compareDates left.Index.FollowUp right.Index.FollowUp

                if time <> 0 then
                    time
                else
                    StringComparer.Ordinal.Compare(reference left, reference right)

            let after (record: StoredCase) =
                match query.AfterPosition with
                | None -> true
                | Some(CaseListPosition.Reference value) ->
                    StringComparer.Ordinal.Compare(reference record, value) > 0
                | Some(CaseListPosition.Recent(time, value)) ->
                    record.Index.ChangedAt < time
                    || (record.Index.ChangedAt = time
                        && StringComparer.Ordinal.Compare(reference record, value) > 0)
                | Some(CaseListPosition.Notified(date, value)) ->
                    dateAfter
                        record.Index.NotifiedOn
                        date
                        (StringComparer.Ordinal.Compare(reference record, value) > 0)
                | Some(CaseListPosition.FollowUp(date, value)) ->
                    dateAfter
                        record.Index.FollowUp
                        date
                        (StringComparer.Ordinal.Compare(reference record, value) > 0)


            let rows =
                state.Cases
                |> Map.values
                |> Seq.filter (fun record -> selected state query.Selection record && after record)
                |> Seq.sortWith compareRows
                |> Seq.map (readCase (get ()).Entries)
                |> Seq.toList

            let selected =
                page
                    query.Limit
                    (fun record ->
                        CaseListPosition.create
                            query.Filter.Order
                            record.Index.ChangedAt
                            record.Index.NotifiedOn
                            record.Index.FollowUp
                            (CaseReference.format record.Snapshot.Reference))
                    rows

            Task.FromResult(
                {
                    Items =
                        selected.Items
                        |> List.map (fun record ->
                            {
                                CasePageItem.Case = record
                                WorkSummary =
                                    workSummary
                                        state
                                        query.Selection.DateBasis.BusinessDate
                                        record.CaseId
                            })
                    Next = selected.Next
                }
            )

        member _.CaseSummary selection =
            read ()
            let state = get ()

            let groups =
                state.Cases
                |> Map.values
                |> Seq.filter (selected state selection)
                |> Seq.map (fun record ->
                    {
                        CaseCountGroup.Responsibility =
                            if record.Index.Responsible = Some selection.ActorId then
                                Some CaseAssignment.Mine
                            elif
                                record.Index.Responsible.IsNone
                                && CaseState.lifecycle record.Snapshot.State <> Lifecycle.Erased
                            then
                                Some CaseAssignment.Unassigned
                            else
                                None
                        Due =
                            CaseDue.at
                                selection.DateBasis.BusinessDate
                                (CaseState.lifecycle record.Snapshot.State)
                                record.Index.Status
                                record.Index.FollowUp
                        Lifecycle = CaseState.lifecycle record.Snapshot.State
                        Status = record.Index.Status
                        Progress = record.Index.Progress
                        Count = 1L
                        Claimed = total record.Index.Claimed
                        Payable = total record.Index.Payable
                    })
                |> Seq.toList

            Task.FromResult(
                CaseWorklistSummary.fromGroups selection.DateBasis groups
                |> Result.defaultWith failwith
            )

        member _.HistoryPage
            ({
                 HistoryPageQuery.CaseId = id
                 AfterRevision = after
                 Limit = limit
             })
            =
            read ()

            let rows =
                (get ()).Entries
                |> Map.values
                |> Seq.choose (fun entry ->
                    match entry.Subject with
                    | EntryTarget.Case(caseId, revision, _, _) when
                        caseId = id && (after = Revision.zero || revision < after)
                        ->
                        Some(revision, copyEntry entry)
                    | _ -> None)
                |> Seq.sortByDescending fst
                |> Seq.map snd
                |> Seq.toList

            let revision (entry: StoredEntry) =
                match entry.Subject with
                | EntryTarget.Case(_, revision, _, _) -> revision
                | _ -> failwith "Invalid history fixture"

            Task.FromResult
                {
                    Page = page limit revision rows
                    Previous = List.tryItem limit rows
                }

        member _.AuthorityHistoryPage(query: AuthorityHistoryPageQuery) =
            read ()

            let rows =
                (get ()).Entries
                |> Map.values
                |> Seq.filter (fun entry ->
                    match entry.Subject with
                    | EntryTarget.Authority(target, _) ->
                        (query.TargetActorId |> Option.forall ((=) target))
                        && (query.BeforeSequence
                            |> Option.forall (fun before -> entry.Sequence < before))
                    | _ -> false)
                |> Seq.sortByDescending _.Sequence
                |> Seq.map copyEntry
                |> Seq.toList

            Task.FromResult(page query.Limit (fun entry -> entry.Sequence) rows)

        member _.AccessRequestPage query =
            read ()

            let key principal =
                let kind, issuer, subject = PrincipalKey.parts principal
                issuer, DurableNames.principalKind kind, subject

            let compare left right =
                let dates = compare right.LastSeen left.LastSeen

                if dates <> 0 then
                    dates
                else
                    compare (key left.Principal) (key right.Principal)

            let items =
                get().Requests
                |> Map.values
                |> Seq.filter (fun value -> value.LastSeen >= query.ObservedAt.AddDays(-30))
                |> Seq.sortWith compare
                |> Seq.filter (fun value ->
                    query.After
                    |> Option.forall (fun after ->
                        compare
                            value
                            {
                                AccessRequest.Principal = after.Principal
                                FirstSeen = after.LastSeen
                                LastSeen = after.LastSeen
                                Claims = RequesterClaims.empty
                            }
                            >
                            0))
                |> Seq.toList

            Task.FromResult(
                page
                    query.Limit
                    (fun value ->
                        {
                            AccessRequestPosition.LastSeen = value.LastSeen
                            Principal = value.Principal
                        })
                    items
            )

        member _.ActorPage
            ({
                 ActorPageQuery.AfterActorId = after
                 Limit = limit
             })
            =
            read ()

            let rows =
                (get ()).Actors
                |> Map.values
                |> Seq.filter (fun actor ->
                    after
                    |> Option.forall (fun after ->
                        compareId ActorId.format actor.ActorId after > 0))
                |> Seq.sortWith (fun a b -> compareId ActorId.format a.ActorId b.ActorId)
                |> Seq.toList

            Task.FromResult(page limit (fun actor -> actor.ActorId) rows)

        member _.GrantPage
            ({
                 GrantPageQuery.ActorId = actor
                 AfterGrant = after
                 Limit = limit
             })
            =
            read ()
            let state = get ()

            let compareScope a b =
                match a, b with
                | None, None -> 0
                | None, Some _ -> 1
                | Some _, None -> -1
                | Some a, Some b -> compareId CaseId.format a b

            let rows =
                state.Grants
                |> Seq.choose (fun (id, role, scope) ->
                    if id <> actor then
                        None
                    else
                        let position = { Role = role; CaseId = scope }

                        let included =
                            after
                            |> Option.forall (fun after ->
                                let ordering =
                                    StringComparer.Ordinal.Compare(
                                        Role.token role,
                                        Role.token after.Role
                                    )

                                ordering > 0
                                || (ordering = 0 && compareScope scope after.CaseId > 0))

                        if not included then
                            None
                        else
                            let target =
                                match scope with
                                | None -> GrantScope.Installation
                                | Some id ->
                                    let current = readCase state.Entries state.Cases[id]
                                    GrantScope.Case(id, current.Snapshot.Reference)

                            Some(
                                {
                                    ScopedGrant.Role = role
                                    Scope = target
                                },
                                position
                            ))
                |> Seq.sortWith (fun (_, a) (_, b) ->
                    let role = StringComparer.Ordinal.Compare(Role.token a.Role, Role.token b.Role)

                    if role = 0 then compareScope a.CaseId b.CaseId else role)
                |> Seq.toList

            let visible = page limit snd rows

            Task.FromResult
                {
                    Items = visible.Items |> List.map fst
                    Next = visible.Next
                }
