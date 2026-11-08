namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Current account labels/availability are batched for the page under its read snapshot.
module internal CaseWorkReads =
    let facts (record: StoredCase) (work: CaseWorkView) : CaseWorkFacts =
        {
            State =
                record.Snapshot.State
                |> CaseState.map (fun fields ->
                    fields.Status, CaseProgress.ofPayment fields.Progress)
            Responsible =
                work.Responsible
                |> Option.map (fun assigned ->
                    assigned.ActorId, assigned.Availability = ActorAvailability.Available)
            NextPendingDue = work.FollowUp
        }

    let private agrees (record: StoredCase) erased (summary: CaseWorkSummary option) =
        match summary with
        | None ->
            erased
            && record.Index.Responsible.IsNone
            && record.Index.FollowUp.IsNone
            && record.Index.WorkSearchText.IsNone
        | Some summary ->
            let payload = summary.Work.Snapshot
            let responsible = payload |> Option.bind _.Work.Responsible
            let followUp = summary.Next |> Option.map Reminder.due

            summary.Work.CaseId = record.CaseId
            && (erased = payload.IsNone)
            && (payload
                |> Option.forall (fun value ->
                    value.CaseId = record.CaseId && value.Revision = summary.Work.Revision))
            && (summary.Next
                |> Option.forall (fun value -> Reminder.caseId value = record.CaseId))
            && (payload |> Option.bind _.Work.Summary |> Option.isSome) =
                summary.Work.SummaryChangedAt.IsSome
            && summary.Work.SummaryChangedAt.IsSome = summary.Work.SummaryRecordedBy.IsSome
            && record.Index.Responsible = responsible
            && record.Index.FollowUp = followUp
            && record.Index.WorkSearchText =
                (payload
                 |> Option.bind _.Work.Summary
                 |> Option.map (HandlingSummary.format >> TextSearch.normalize))

    let viewsFromSummaries
        (tx: IReadTx)
        basis
        (records: StoredCase list)
        (summaries: Map<CaseId, CaseWorkSummary>)
        =
        task {
            let assignments =
                summaries
                |> Map.toList
                |> List.choose (fun (id, summary) ->
                    summary.Work.Snapshot
                    |> Option.bind (fun snapshot ->
                        snapshot.Work.Responsible |> Option.map (fun actor -> id, actor)))

            let! accounts = tx.AssignedAccounts assignments

            let authorIds =
                summaries
                |> Map.values
                |> Seq.collect (fun summary ->
                    [ summary.Work.RecordedBy; summary.Work.SummaryRecordedBy ] |> List.choose id)
                |> Seq.distinct
                |> Seq.toList

            let! authors = tx.ActorRefs authorIds
            let mutable result = Ok Map.empty

            for record in records do
                let erased = CaseState.lifecycle record.Snapshot.State = Lifecycle.Erased

                match Map.tryFind record.CaseId summaries with
                | None when not erased -> result <- Error CoreFault.StoreIntegrityError
                | summary ->
                    if not (agrees record erased summary) then
                        result <- Error CoreFault.StoreIntegrityError

                    let work =
                        summary
                        |> Option.bind _.Work.Snapshot
                        |> Option.map _.Work
                        |> Option.defaultValue CaseWork.empty

                    let responsible =
                        work.Responsible
                        |> Option.bind (fun actor -> Map.tryFind (record.CaseId, actor) accounts)

                    if work.Responsible.IsSome && responsible.IsNone then
                        result <- Error CoreFault.StoreIntegrityError

                    let fields = CaseState.fields record.Snapshot.State
                    let next = summary |> Option.bind _.Next
                    let followUp = next |> Option.map Reminder.due

                    let due =
                        CaseDue.at
                            basis.BusinessDate
                            (CaseState.lifecycle record.Snapshot.State)
                            (fields |> Option.map _.Status)
                            followUp

                    let pending = summary |> Option.map _.Pending |> Option.defaultValue 0L
                    let actionable = due <> CaseDue.Inactive

                    let view =
                        {
                            CaseWorkView.Revision =
                                summary
                                |> Option.map _.Work.Revision
                                |> Option.defaultValue Revision.zero
                            ChangedAt =
                                if erased then
                                    None
                                else
                                    summary |> Option.bind _.Work.ChangedAt
                            RecordedBy =
                                if erased then
                                    None
                                else
                                    summary
                                    |> Option.bind (fun value ->
                                        value.Work.RecordedBy
                                        |> Option.bind (fun id ->
                                            match Map.tryFind id authors with
                                            | Some actor -> Some actor
                                            | None ->
                                                result <- Error CoreFault.StoreIntegrityError
                                                None))
                            Summary = if erased then None else work.Summary
                            SummaryChangedAt =
                                if erased then
                                    None
                                else
                                    summary |> Option.bind _.Work.SummaryChangedAt
                            SummaryRecordedBy =
                                if erased then
                                    None
                                else
                                    summary
                                    |> Option.bind (fun value ->
                                        value.Work.SummaryRecordedBy
                                        |> Option.bind (fun id ->
                                            match Map.tryFind id authors with
                                            | Some actor -> Some actor
                                            | None ->
                                                result <- Error CoreFault.StoreIntegrityError
                                                None))
                            NextAction = if erased then None else next |> Option.map Reminder.action
                            PendingReminders = if erased then 0L else pending
                            OverdueReminders =
                                if actionable then
                                    summary |> Option.map _.Overdue |> Option.defaultValue 0L
                                else
                                    0L
                            NeedsNextAction = actionable && pending = 0L
                            CanUpdateSummary = false
                            CanAssign = false
                            CanCreateReminder = false
                            Responsible = if erased then None else responsible
                            FollowUp = if erased then None else followUp
                            Basis = basis
                            Due = due
                        }

                    result <- result |> Result.map (Map.add record.CaseId view)

            return result
        }

    let views (tx: IReadTx) basis (records: StoredCase list) =
        task {
            let! summaries = tx.WorkSummaries(records |> List.map _.CaseId, basis.BusinessDate)
            return! viewsFromSummaries tx basis records summaries
        }

    let view tx basis record =
        task {
            let! values = views tx basis [ record ]
            return values |> Result.map (Map.find record.CaseId)
        }

    let reminderActs principal actor maintenance lifecycle status value =
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
            [
                ReminderActKind.Update
                ReminderActKind.Complete
                ReminderActKind.CompleteAndPlanNext
                ReminderActKind.Cancel
                ReminderActKind.Reopen
            ]
            |> List.filter (fun act ->
                (ReminderState.after act (Some(Reminder.state value))).IsSome
                && (status = Some CaseStatus.Opened
                    || act = ReminderActKind.Complete
                    || act = ReminderActKind.Cancel))

    /// A bounded set of current pending rows under the same authorized case-read snapshot.
    let nextActions
        (services: ServiceContext)
        (tx: IReadTx)
        (work: CaseWorkView)
        (actor: AuthorityView)
        maintenance
        (record: StoredCase)
        =
        task {
            let basis = work.Basis

            match actor.Actor with
            | Some requester when CaseState.lifecycle record.Snapshot.State <> Lifecycle.Erased ->
                let! page =
                    tx.ReminderPage
                        {
                            Selection =
                                {
                                    DateBasis = basis
                                    Visibility =
                                        if
                                            Authority.can
                                                services.Principal
                                                actor
                                                Capability.ReadCase
                                                Scope.Installation
                                        then
                                            CaseVisibility.Every
                                        else
                                            CaseVisibility.Scoped
                                    ActorId = requester.ActorId
                                    ReadRoles = Authority.readRoles services.Principal
                                    ForbiddenRoles = Authority.invalidRoles services.Principal
                                    Filter = CaseListFilter.empty
                                }
                            TargetCaseId = Some record.CaseId
                            State = Some ReminderState.Pending
                            View = None
                            Due = None
                            Text = None
                            IncludeInactive = true
                            After = None
                            Limit = Paging.MaximumPageSize
                        }

                let! authors =
                    tx.ActorRefs(page.Items |> List.map _.Stored.RecordedBy |> List.distinct)

                return
                    page.Items
                    |> List.map (fun row ->
                        match row.Stored.Snapshot, Map.tryFind row.Stored.RecordedBy authors with
                        | Some reminder, Some author when
                            row.Stored.CaseId = record.CaseId
                            && row.Reference = record.Snapshot.Reference
                            && Reminder.caseId reminder = record.CaseId
                            && Reminder.id reminder = row.Stored.ReminderId
                            && Reminder.revision reminder = row.Stored.Revision
                            && Reminder.state reminder = ReminderState.Pending
                            && row.Responsible = (work.Responsible |> Option.map _.ActorId)
                            && row.Lifecycle = CaseState.lifecycle record.Snapshot.State
                            && row.Status =
                                (CaseState.fields record.Snapshot.State |> Option.map _.Status)
                            ->
                            let active = CaseState.isActionable row.Lifecycle row.Status

                            Ok
                                {
                                    ReminderReadModel.Reminder = reminder
                                    Reference = row.Reference
                                    ClaimantName = row.ClaimantName
                                    Responsible = work.Responsible
                                    RecordedAt = row.Stored.RecordedAt
                                    RecordedBy = author
                                    Acts =
                                        reminderActs
                                            services.Principal
                                            actor
                                            maintenance
                                            row.Lifecycle
                                            row.Status
                                            reminder
                                    Active = active
                                    Timing = ReminderDue.at basis.BusinessDate active reminder
                                }
                        | _ -> Error CoreFault.StoreIntegrityError)
                    |> QueryResults.collect
            | _ when CaseState.lifecycle record.Snapshot.State = Lifecycle.Erased -> return Ok []
            | _ -> return Error CoreFault.StoreIntegrityError
        }
