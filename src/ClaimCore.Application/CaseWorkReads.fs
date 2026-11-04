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
                |> Option.map (fun assigned -> assigned.Account.ActorId, assigned.Available)
            NextPendingDue = work.FollowUp
        }

    let basis (services: ServiceContext) observed =
        {
            ObservedAt = observed
            BusinessDate = services.Calendar.DateAt observed
            TimeZoneId = services.Calendar.TimeZoneId
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
            && record.Index.Responsible = responsible
            && record.Index.FollowUp = followUp
            && record.Index.WorkSearchText =
                (payload
                 |> Option.bind _.Work.Summary
                 |> Option.map (HandlingSummary.format >> CaseSearch.normalize))

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

                    let responsible = accounts |> Map.tryFind record.CaseId

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
                                    summary |> Option.bind _.Work.RecordedBy
                            Summary = if erased then None else work.Summary
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
