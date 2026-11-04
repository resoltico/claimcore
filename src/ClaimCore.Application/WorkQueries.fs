namespace ClaimCore.Application

open System
open System.Security.Cryptography
open System.Text
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Current read authority and date basis are acquired with every page; continuations grant no access.
module internal WorkQueries =
    let private scope parts =
        String.concat "\u001f" parts
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexStringLower

    let private selection (services: ServiceContext) (tx: IReadTx) actor mine =
        QueryResults.currentActor services.Principal actor
        |> Result.bind (fun _ ->
            match Authority.visibility services.Principal actor, actor.Actor with
            | Some visibility, Some requester ->
                Ok
                    {
                        CaseSelection.DateBasis = CaseWorkReads.basis services tx.ObservedAt
                        Visibility = visibility
                        ActorId = requester.ActorId
                        ReadRoles = Authority.readRoles services.Principal
                        ForbiddenRoles = Authority.invalidRoles services.Principal
                        Filter =
                            { CaseListFilter.empty with
                                Assignment = if mine then Some CaseAssignment.Mine else None
                            }
                    }
            | _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable))

    let overview (services: ServiceContext) (request: WorkOverviewRequest) token =
        task {
            let! result =
                services.Store.Read(
                    {
                        Work =
                            fun tx ->
                                task {
                                    let! actor =
                                        tx.Authority
                                            {
                                                Principal = services.Principal
                                                CaseId = None
                                            }

                                    match selection services tx actor request.Mine with
                                    | Error refusal -> return Outcome.Rejected refusal
                                    | Ok selection ->
                                        let! overview = tx.WorkOverview(selection, request.Mine)
                                        return Outcome.Succeeded overview
                                }
                        CancellationToken = token
                    }
                )

            return QueryResults.readResult result
        }

    let private reminderActs principal actor maintenance lifecycle status value =
        let permitted =
            not maintenance
            && lifecycle = Lifecycle.Active
            && Authority.can principal actor Capability.ReadCase (Scope.Case(Reminder.caseId value))
            && Authority.can
                principal
                actor
                Capability.ManageCaseWork
                (Scope.Case(Reminder.caseId value))

        if not permitted then
            []
        else
            match Reminder.state value, status with
            | ReminderState.Pending, Some CaseStatus.Opened ->
                [
                    ReminderActKind.Update
                    ReminderActKind.Complete
                    ReminderActKind.CompleteAndPlanNext
                    ReminderActKind.Cancel
                ]
            | ReminderState.Pending, _ -> [ ReminderActKind.Complete; ReminderActKind.Cancel ]
            | (ReminderState.Completed | ReminderState.Cancelled), Some CaseStatus.Opened ->
                [ ReminderActKind.Reopen ]
            | _ -> []

    let reminder (services: ServiceContext) (request: ReminderGetRequest) token =
        task {
            match
                CaseExecution.validateSelector request.Target, ReminderId.create request.ReminderId
            with
            | Error refusal, _ -> return Outcome.Rejected refusal
            | _, None ->
                return
                    Outcome.Rejected(
                        Refusal.invalidInput (InputTarget.ReminderId, Violation.ReminderIdRequired)
                    )
            | Ok(), Some id ->
                let! result =
                    services.Store.Read(
                        {
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
                                                Capability.ReadCase
                                                (Scope.Case parent.CaseId)
                                            ->
                                            let! value = tx.FindReminder id

                                            match value with
                                            | Some value when value.ReminderId <> id ->
                                                return Outcome.Failed CoreFault.StoreIntegrityError
                                            | Some value when
                                                value.CaseId = parent.CaseId
                                                && value.Snapshot.IsSome
                                                && CaseState.lifecycle parent.Snapshot.State
                                                   <> Lifecycle.Erased
                                                ->
                                                let! authors = tx.ActorRefs [ value.RecordedBy ]

                                                match Map.tryFind value.RecordedBy authors with
                                                | None ->
                                                    return
                                                        Outcome.Failed CoreFault.StoreIntegrityError
                                                | Some author ->
                                                    let reminder = value.Snapshot.Value

                                                    let lifecycle =
                                                        CaseState.lifecycle parent.Snapshot.State

                                                    let status =
                                                        CaseState.fields parent.Snapshot.State
                                                        |> Option.map _.Status

                                                    let basis =
                                                        CaseWorkReads.basis services tx.ObservedAt

                                                    let active =
                                                        CaseState.isActionable lifecycle status

                                                    return
                                                        Outcome.Succeeded
                                                            {
                                                                ReminderRead.Basis = basis
                                                                Item =
                                                                    {
                                                                        Reminder = reminder
                                                                        Reference =
                                                                            parent.Snapshot
                                                                                .Reference
                                                                        RecordedAt =
                                                                            value.RecordedAt
                                                                        RecordedBy = author
                                                                        Acts =
                                                                            reminderActs
                                                                                services.Principal
                                                                                view.Actor
                                                                                view.Maintenance
                                                                                    .Active
                                                                                lifecycle
                                                                                status
                                                                                reminder
                                                                        Active = active
                                                                        Timing =
                                                                            ReminderDue.at
                                                                                basis.BusinessDate
                                                                                active
                                                                                reminder
                                                                    }
                                                            }
                                            | _ -> return QueryResults.unavailable
                                        | _ -> return QueryResults.unavailable
                                    }
                            CancellationToken = token
                        }
                    )

                return QueryResults.readResult result
        }

    let reminders (services: ServiceContext) (request: ReminderListRequest) token =
        task {
            if not (QueryResults.validLimit request.Limit) then
                return QueryResults.invalidLimit ()
            else
                let text =
                    match request.Text with
                    | None -> Ok None
                    | Some raw ->
                        CaseSearchText.parse raw
                        |> Result.map Some
                        |> Result.mapError (fun issue ->
                            Refusal.invalidInput (InputTarget.Text, issue))

                let selector =
                    request.Target
                    |> Option.map CaseExecution.validateSelector
                    |> Option.defaultValue (Ok())

                match Admission.map2 (fun () text -> text) selector text with
                | Error refusal -> return Outcome.Rejected refusal
                | Ok text ->
                    let! result =
                        services.Store.Read(
                            {
                                Work =
                                    fun tx ->
                                        task {
                                            let! actor =
                                                tx.Authority
                                                    {
                                                        Principal = services.Principal
                                                        CaseId = None
                                                    }

                                            let! parent =
                                                match request.Target with
                                                | Some target -> ExecutionFacts.findCase tx target
                                                | None ->
                                                    System.Threading.Tasks.Task.FromResult None

                                            let! targetActor =
                                                match parent with
                                                | Some parent ->
                                                    tx.AuthorityCases(
                                                        services.Principal,
                                                        [ parent.CaseId ]
                                                    )
                                                | None ->
                                                    System.Threading.Tasks.Task.FromResult actor

                                            let targetAllowed =
                                                match request.Target, parent with
                                                | None, _ -> true
                                                | Some _, Some parent ->
                                                    Authority.can
                                                        services.Principal
                                                        targetActor
                                                        Capability.ReadCase
                                                        (Scope.Case parent.CaseId)
                                                | _ -> false

                                            match
                                                selection
                                                    services
                                                    tx
                                                    actor
                                                    (request.Assignment = Some CaseAssignment.Mine)
                                            with
                                            | Error refusal -> return Outcome.Rejected refusal
                                            | Ok _ when not targetAllowed ->
                                                return QueryResults.unavailable
                                            | Ok selection ->
                                                let selection =
                                                    { selection with
                                                        Filter =
                                                            { selection.Filter with
                                                                Assignment = request.Assignment
                                                            }
                                                    }

                                                let state =
                                                    match request.State, request.View with
                                                    | None, None -> Some ReminderState.Pending
                                                    | value, _ -> value

                                                let includeInactive =
                                                    defaultArg
                                                        request.IncludeInactive
                                                        request.View.IsSome

                                                let parentId = parent |> Option.map _.CaseId

                                                let binding =
                                                    scope
                                                        [
                                                            ActorId.format selection.ActorId
                                                            string selection.Visibility
                                                            string selection.ReadRoles
                                                            string selection.ForbiddenRoles
                                                            parentId
                                                            |> Option.map CaseId.format
                                                            |> Option.defaultValue ""
                                                            state
                                                            |> Option.map DurableNames.reminderState
                                                            |> Option.defaultValue ""
                                                            request.View
                                                            |> Option.map DurableNames.reminderView
                                                            |> Option.defaultValue ""
                                                            request.Due
                                                            |> Option.map ReminderDue.token
                                                            |> Option.defaultValue ""
                                                            request.Assignment
                                                            |> Option.map CaseAssignment.token
                                                            |> Option.defaultValue ""
                                                            text
                                                            |> Option.map CaseSearchText.value
                                                            |> Option.defaultValue ""
                                                            string includeInactive
                                                        ]

                                                match
                                                    QueryResults.cursor
                                                        (Cursors.decodeReminders
                                                            services.InstallationId
                                                            binding)
                                                        request.AfterCursor
                                                with
                                                | Error refusal -> return Outcome.Rejected refusal
                                                | Ok after ->
                                                    let! maintenance = tx.Maintenance()

                                                    let! page =
                                                        tx.ReminderPage
                                                            {
                                                                Selection = selection
                                                                TargetCaseId = parentId
                                                                State = state
                                                                View = request.View
                                                                Due = request.Due
                                                                Text = text
                                                                IncludeInactive = includeInactive
                                                                After =
                                                                    after
                                                                    |> Option.map (fun (due, id) ->
                                                                        {
                                                                            ReminderPosition.Due =
                                                                                due
                                                                            ReminderId = id
                                                                        })
                                                                Limit = request.Limit
                                                            }

                                                    let! pageActor =
                                                        tx.AuthorityCases(
                                                            services.Principal,
                                                            page.Items
                                                            |> List.map _.Stored.CaseId
                                                            |> List.distinct
                                                        )

                                                    let! authors =
                                                        tx.ActorRefs(
                                                            page.Items
                                                            |> List.map _.Stored.RecordedBy
                                                            |> List.distinct
                                                        )

                                                    let items =
                                                        page.Items
                                                        |> List.map (fun row ->
                                                            match
                                                                row.Stored.Snapshot,
                                                                Map.tryFind
                                                                    row.Stored.RecordedBy
                                                                    authors
                                                            with
                                                            | None, _
                                                            | _, None ->
                                                                Error CoreFault.StoreIntegrityError
                                                            | Some reminder, Some author ->
                                                                let active =
                                                                    CaseState.isActionable
                                                                        row.Lifecycle
                                                                        row.Status

                                                                Ok
                                                                    {
                                                                        ReminderReadModel.Reminder =
                                                                            reminder
                                                                        Reference = row.Reference
                                                                        RecordedAt =
                                                                            row.Stored.RecordedAt
                                                                        RecordedBy = author
                                                                        Acts =
                                                                            reminderActs
                                                                                services.Principal
                                                                                pageActor
                                                                                maintenance.Active
                                                                                row.Lifecycle
                                                                                row.Status
                                                                                reminder
                                                                        Active = active
                                                                        Timing =
                                                                            ReminderDue.at
                                                                                selection.DateBasis
                                                                                    .BusinessDate
                                                                                active
                                                                                reminder
                                                                    })
                                                        |> QueryResults.collect

                                                    return
                                                        items
                                                        |> Result.map (fun items ->
                                                            {
                                                                ReminderPage.Basis =
                                                                    selection.DateBasis
                                                                Items = items
                                                                NextCursor =
                                                                    page.Next
                                                                    |> Option.map (fun position ->
                                                                        Cursors.encodeReminders
                                                                            services
                                                                                .InstallationId
                                                                            binding
                                                                            position.Due
                                                                            position.ReminderId)
                                                            })
                                                        |> Result.map Outcome.Succeeded
                                                        |> Result.defaultWith Outcome.Failed
                                        }
                                CancellationToken = token
                            }
                        )

                    return QueryResults.readResult result
        }

    let activity (services: ServiceContext) (request: CaseActivityRequest) token =
        task {
            if not (QueryResults.validLimit request.Limit) then
                return QueryResults.invalidLimit ()
            else
                let! result =
                    services.Store.Read(
                        {
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
                                                (if request.Detail = HistoryDetail.Full then
                                                     Capability.ReadHistoryFull
                                                 else
                                                     Capability.ReadHistory)
                                                (Scope.Case parent.CaseId)
                                            ->
                                            let actor = view.Actor.Actor.Value.ActorId

                                            let binding =
                                                scope
                                                    [
                                                        CaseId.format parent.CaseId
                                                        ActorId.format actor
                                                        string request.Detail
                                                    ]

                                            match
                                                QueryResults.cursor
                                                    (Cursors.decodeActivity
                                                        services.InstallationId
                                                        binding)
                                                    request.AfterCursor
                                            with
                                            | Error refusal -> return Outcome.Rejected refusal
                                            | Ok before ->
                                                let! page =
                                                    tx.ActivityPage
                                                        {
                                                            CaseId = parent.CaseId
                                                            BeforeSequence = before
                                                            Limit = request.Limit
                                                        }

                                                let! authors =
                                                    tx.ActorRefs(
                                                        page.Items
                                                        |> List.map _.ActorId
                                                        |> List.distinct
                                                    )

                                                let mutable items = Ok []

                                                for entry in page.Items do
                                                    let subject =
                                                        match entry.Subject with
                                                        | EntryTarget.Case(id, revision, act, _) when
                                                            id = parent.CaseId
                                                            ->
                                                            Ok(ActivitySubject.Case(revision, act))
                                                        | EntryTarget.Work subject when
                                                            subject.CaseId = parent.CaseId
                                                            ->
                                                            Ok(
                                                                ActivitySubject.Work(
                                                                    subject.Revision,
                                                                    subject.Act
                                                                )
                                                            )
                                                        | EntryTarget.Reminder subject when
                                                            subject.CaseId = parent.CaseId
                                                            ->
                                                            Ok(
                                                                ActivitySubject.Reminder(
                                                                    subject.Act,
                                                                    subject.ReminderId,
                                                                    subject.Revision,
                                                                    subject.NextReminderId
                                                                    |> Option.map (fun id ->
                                                                        id, Revision.first)
                                                                )
                                                            )
                                                        | _ -> Error CoreFault.StoreIntegrityError

                                                    let! _, _, current =
                                                        ExecutionFacts.operation
                                                            tx
                                                            services.Principal
                                                            entry.OperationId

                                                    let detail =
                                                        LedgerEntries.validate
                                                            services.InstallationId
                                                            entry
                                                        |> Result.bind (fun validated ->
                                                            if
                                                                request.Detail = HistoryDetail.Full
                                                            then
                                                                LedgerEntries.receipt
                                                                    services.Principal
                                                                    view.Actor
                                                                    (Map.tryFind
                                                                        entry.ActorId
                                                                        authors)
                                                                    current
                                                                    validated
                                                                |> Result.map Some
                                                            else
                                                                Ok None)

                                                    items <-
                                                        items
                                                        |> Result.bind (fun items ->
                                                            match
                                                                subject,
                                                                detail,
                                                                Map.tryFind entry.ActorId authors
                                                            with
                                                            | Ok subject, Ok detail, Some author ->
                                                                Ok(
                                                                    {
                                                                        CaseActivity.OperationId =
                                                                            entry.OperationId
                                                                        Position =
                                                                            LedgerEntries.position
                                                                                entry.Sequence
                                                                                entry.EntryHash
                                                                        Subject = subject
                                                                        RecordedAt =
                                                                            entry.RecordedAt
                                                                        RecordedBy = author
                                                                        Detail = detail
                                                                    }
                                                                    :: items
                                                                )
                                                            | Error fault, _, _
                                                            | _, Error fault, _ -> Error fault
                                                            | _, _, None ->
                                                                Error CoreFault.StoreIntegrityError)

                                                return
                                                    items
                                                    |> Result.map (fun items ->
                                                        {
                                                            CaseActivityPage.CaseId = parent.CaseId
                                                            Reference = parent.Snapshot.Reference
                                                            Items = List.rev items
                                                            NextCursor =
                                                                page.Next
                                                                |> Option.map (
                                                                    Cursors.encodeActivity
                                                                        services.InstallationId
                                                                        binding
                                                                )
                                                        })
                                                    |> Result.map Outcome.Succeeded
                                                    |> Result.defaultWith Outcome.Failed
                                        | _ -> return QueryResults.unavailable
                                    }
                            CancellationToken = token
                        }
                    )

                return QueryResults.readResult result
        }
