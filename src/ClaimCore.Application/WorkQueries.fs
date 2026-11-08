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
                        CaseSelection.DateBasis = ServiceContext.basis services tx.ObservedAt
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

    let reminder (services: ServiceContext) (request: ReminderGetRequest) token =
        task {
            match
                CaseExecution.validateSelector request.Target, ReminderId.create request.ReminderId
            with
            | Error refusal, _ -> return! RequestRefusals.read services token refusal
            | _, None ->
                return!
                    RequestRefusals.read
                        services
                        token
                        (Refusal.invalidInput (InputTarget.ReminderId, Violation.ReminderIdRequired))
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
                                                        ServiceContext.basis services tx.ObservedAt

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
                                                                            CaseWorkReads
                                                                                .reminderActs
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
                return! RequestRefusals.invalidLimit services token
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
                | Error refusal -> return! RequestRefusals.read services token refusal
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
                                                            |> Option.map ReminderState.token
                                                            |> Option.defaultValue ""
                                                            request.View
                                                            |> Option.map ReminderView.token
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
                                                                            CaseWorkReads
                                                                                .reminderActs
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
