namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module CaseScreen =
    let private scalarGroups = [ RecordGroup.Registration; RecordGroup.Decision ]

    let caseFacts (snapshot: CaseSnapshot) =
        match CaseState.fields snapshot.State with
        | None -> []
        | Some facts ->
            let values =
                CaseFields.declarations
                |> List.map (fun field -> field.Field, CaseFields.typedValue field.Field facts)
                |> Map.ofList

            scalarGroups
            |> List.map (fun group ->
                let declarations =
                    CaseFields.declarations |> List.filter (fun field -> field.Group = Some group)

                {
                    TitleId = Vocabulary.messageKey RecordGroup.vocabulary group "label"
                    Entries = entries (fields declarations (fun () -> Some values)) ()
                })

    let caseWork (work: CaseWorkView) =
        {
            TitleId = "section.work.label"
            Entries =
                [
                    value
                        "responsible"
                        "field.responsibleActor.label"
                        Prominence.Primary
                        (work.Responsible
                         |> Option.map PresentedValue.Actor
                         |> Option.defaultValue (PresentedValue.Absent "view.UNASSIGNED.label"))
                    value
                        "handlingSummary"
                        "field.handlingSummary.label"
                        Prominence.Secondary
                        (optional (HandlingSummary.format >> PresentedValue.Narrative) work.Summary)
                    value
                        "nextAction"
                        "field.reminderAction.label"
                        Prominence.Primary
                        (optional (ReminderAction.format >> PresentedValue.Text) work.NextAction)
                    value
                        "followUp"
                        "label.nextFollowUp"
                        Prominence.Secondary
                        (optional PresentedValue.Date work.FollowUp)
                    value
                        "pendingReminders"
                        "label.pendingReminders"
                        Prominence.Secondary
                        (PresentedValue.Count work.PendingReminders)
                    value
                        "overdueReminders"
                        "label.overdueReminders"
                        Prominence.Secondary
                        (PresentedValue.Count work.OverdueReminders)
                    value
                        "summaryChangedAt"
                        "label.summaryChangedAt"
                        Prominence.Secondary
                        (optional PresentedValue.Instant work.SummaryChangedAt)
                    value
                        "summaryRecordedBy"
                        "label.summaryRecordedBy"
                        Prominence.Secondary
                        (optional PresentedValue.Actor work.SummaryRecordedBy)
                    value
                        "workRevision"
                        "label.workRevision"
                        Prominence.Detail
                        (PresentedValue.Identifier(Revision.format work.Revision))
                    value
                        "workChangedAt"
                        "label.workChangedAt"
                        Prominence.Detail
                        (optional PresentedValue.Instant work.ChangedAt)
                    value
                        "workRecordedBy"
                        "label.workRecordedBy"
                        Prominence.Detail
                        (optional PresentedValue.Actor work.RecordedBy)
                ]
        }


    let identityFacts (snapshot: CaseSnapshot) =
        CaseState.fields snapshot.State
        |> Option.map (fun facts ->
            let selected =
                CaseFields.declarations
                |> List.filter (fun field ->
                    List.contains
                        field.Field
                        [
                            BusinessField.ClaimantName
                            BusinessField.InsurerName
                            BusinessField.ClaimedAmount
                        ])

            let values =
                CaseFields.declarations
                |> List.map (fun field -> field.Field, CaseFields.typedValue field.Field facts)
                |> Map.ofList

            entries (fields selected (fun () -> Some values)) ())
        |> Option.defaultValue []


    let view (reply: CaseReadModel) =
        let live = CaseState.lifecycle reply.Snapshot.State <> Lifecycle.Erased
        let groups = caseFacts reply.Snapshot

        let headlines = identityFacts reply.Snapshot

        let identity =
            [
                value
                    "caseReference"
                    "field.caseReference.label"
                    Prominence.Primary
                    (PresentedValue.Reference reply.Snapshot.Reference)
                match CaseState.fields reply.Snapshot.State with
                | Some facts ->
                    if CaseState.lifecycle reply.Snapshot.State = Lifecycle.Voided then
                        value
                            "lifecycle"
                            "input.lifecycle.label"
                            Prominence.Primary
                            (code Lifecycle.vocabulary Lifecycle.Voided)
                    else
                        value
                            "status"
                            "input.status.label"
                            Prominence.Primary
                            (code CaseStatus.vocabulary facts.Status)

                    value
                        "progress"
                        "input.progress.label"
                        Prominence.Primary
                        (code CaseProgress.vocabulary (CaseProgress.ofPayment facts.Progress))

                    if reply.Mine then
                        value
                            "assignment"
                            "input.assignment.label"
                            Prominence.Secondary
                            (code CaseAssignment.vocabulary CaseAssignment.Mine)
                | None -> ()
                yield! headlines
            ]

        let notice =
            match reply.Snapshot.State, reply.Disposition with
            | CaseState.Erased, _ ->
                Some
                    {
                        MessageId = "lifecycle.ERASED.meaning"
                        Reason = None
                        Tone = NoticeTone.Final
                    }
            | _, Some(CaseDisposition.Voided reason) ->
                Some
                    {
                        MessageId = "lifecycle.VOIDED.meaning"
                        Reason = Some(PresentedValue.Text(Reason.format reason))
                        Tone = NoticeTone.Warning
                    }
            | _, Some(CaseDisposition.Reinstated reason) ->
                Some
                    {
                        MessageId = "label.stageReinstated"
                        Reason = Some(PresentedValue.Text(Reason.format reason))
                        Tone = NoticeTone.Info
                    }
            | _ -> None

        let progress =
            match reply.Stages with
            | CaseStageDisplay.Erased -> None
            | CaseStageDisplay.Tracker(current, steps) ->
                Some
                    {
                        RegionStages.Current = current
                        Steps =
                            steps
                            |> List.map (fun step ->
                                {
                                    RegionStage.Stage = step.Stage
                                    Reached = step.Completed
                                    Date = step.Date
                                    Meter =
                                        if step.Stage = CaseStageKind.Paid then
                                            CaseState.fields reply.Snapshot.State
                                            |> Option.bind (fun fields ->
                                                PaymentsScreen.meter fields.Progress)
                                        else
                                            None
                                    Amount =
                                        step.Amount
                                        |> Option.map (fun money ->
                                            {
                                                PresentedMoney.Value = money
                                                DisplayScale = Money.displayScale money
                                            })
                                })
                    }

        let actions =
            let recommended = reply.Advice.Recommended |> Option.map ActKind.Case

            let offered =
                reply.Advice.Acts
                |> List.filter ((<>) CaseActKind.RemovePayment)
                |> List.filter (fun act -> Some(ActKind.Case act) <> recommended)

            {
                RegionActions.Recommended = recommended
                Groups =
                    Vocabulary.all ActFamily.vocabulary
                    |> List.choose (fun family ->
                        let acts =
                            offered
                            |> List.filter (fun act -> (CaseActKind.describe act).Family = family)
                            |> List.map ActKind.Case

                        if acts.IsEmpty then
                            None
                        else
                            Some
                                {
                                    RegionActionGroup.Family = family
                                    Acts = acts
                                })
            }

        let work =
            (caseWork reply.Work).Entries
            |> List.filter (fun entry ->
                List.contains
                    entry.Key
                    [ "responsible"; "handlingSummary"; "summaryChangedAt"; "summaryRecordedBy" ])

        let nextColumns =
            ReminderListScreen.reminderColumns
            |> List.filter (fun column -> List.contains column.Key [ "action"; "due"; "timing" ])

        let next =
            table
                (fun (row: ReminderReadModel) -> ReminderId.format (Reminder.id row.Reminder))
                (fun _ -> None)
                nextColumns
                reply.NextActions

        let next =
            { next with
                Sections =
                    [
                        section
                            "label.reminder"
                            [
                                value
                                    "pending"
                                    "label.pendingReminders"
                                    Prominence.Secondary
                                    (PresentedValue.Count reply.Work.PendingReminders)
                            ]
                    ]
            }

        let details =
            [
                value
                    "caseReference"
                    "field.caseReference.label"
                    Prominence.Detail
                    (PresentedValue.Reference reply.Snapshot.Reference)
                value
                    "caseId"
                    "input.caseId.label"
                    Prominence.Detail
                    (PresentedValue.Identifier(CaseId.format reply.Snapshot.CaseId))
                value
                    "revision"
                    "label.revisionLabel"
                    Prominence.Detail
                    (PresentedValue.Identifier(Revision.format reply.Snapshot.Revision))
                value
                    "workRevision"
                    "label.workRevision"
                    Prominence.Detail
                    (PresentedValue.Identifier(Revision.format reply.Work.Revision))
                value
                    "changedAt"
                    "label.changedAt"
                    Prominence.Detail
                    (PresentedValue.Instant reply.ChangedAt)
                value
                    "recordedBy"
                    "label.recordedBy"
                    Prominence.Detail
                    (PresentedValue.Actor reply.RecordedBy)
            ]

        Screen.create
            [
                yield entryRegion RegionRole.Identity None identity
                yield!
                    notice
                    |> Option.map (fun notice ->
                        region RegionRole.Notice None (RegionContent.Notice notice))
                    |> Option.toList
                if live then
                    yield
                        region
                            RegionRole.Attention
                            (Some "label.attention")
                            (RegionContent.Codes(
                                Vocabulary.name CaseAttention.vocabulary,
                                reply.Attention
                                |> List.map (Vocabulary.token CaseAttention.vocabulary)
                            ))

                    yield!
                        progress
                        |> Option.map (fun progress ->
                            region
                                RegionRole.Progress
                                (Some "label.stages")
                                (RegionContent.Stages progress))
                        |> Option.toList

                    yield region RegionRole.Recommendation None (RegionContent.Actions actions)

                    for group, facts in List.zip scalarGroups groups do
                        if group = RecordGroup.Registration then
                            yield entryRegion RegionRole.Facts (Some facts.TitleId) facts.Entries

                    match CaseState.fields reply.Snapshot.State with
                    | Some fields ->
                        yield
                            region
                                RegionRole.Payments
                                (Some "label.paymentDecisionAndPayments")
                                (RegionContent.Table(PaymentsScreen.table fields))
                    | None -> ()

                    yield entryRegion RegionRole.Work (Some "section.work.label") work

                    yield
                        region
                            RegionRole.NextActions
                            (Some "label.nextActions")
                            (RegionContent.Table next)
                yield entryRegion RegionRole.Details (Some "label.recordDetails") details
            ]
