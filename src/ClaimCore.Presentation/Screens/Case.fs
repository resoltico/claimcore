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
                FieldPresentation.declarations
                |> List.map (fun field -> field.Field, CaseFields.typedValue field.Field facts)
                |> Map.ofList

            scalarGroups
            |> List.map (fun group ->
                let declarations =
                    FieldPresentation.declarations
                    |> List.filter (fun field -> field.Group = Some group)

                {
                    TitleId = Vocabulary.messageKey RecordGroup.vocabulary group "label"
                    EmptyMessageId = None
                    Entries = entries (fields declarations (fun () -> Some values)) ()
                })

    let caseWork (work: CaseWorkView) =
        {
            TitleId = "section.work.label"
            EmptyMessageId = None
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


    let private identityFacts (snapshot: CaseSnapshot) =
        CaseState.fields snapshot.State
        |> Option.map (fun facts ->
            let selected =
                FieldPresentation.declarations
                |> List.filter (fun field ->
                    List.contains field.Field [ BusinessField.ClaimedAmount ])

            let values =
                FieldPresentation.declarations
                |> List.map (fun field -> field.Field, CaseFields.typedValue field.Field facts)
                |> Map.ofList

            entries (fields selected (fun () -> Some values)) ())
        |> Option.defaultValue []


    let private parties (reply: CaseReadModel) =
        let columns =
            [
                column
                    "role"
                    "field.partyRole.label"
                    Prominence.Primary
                    false
                    (fun (party: CaseParty) -> code PartyRole.vocabulary party.Content.Role)
                column "name" "field.partyName.label" Prominence.Primary false (fun party ->
                    optional PresentedValue.Party party.Content.Name)
                column "details" "field.partyDetails.label" Prominence.Secondary false (fun party ->
                    optional
                        (PartyDetails.format >> PresentedValue.Narrative)
                        party.Content.Details)
            ]

        let table =
            tableWithControls
                (fun (party: CaseParty) -> PartyId.format party.PartyId)
                (fun _ -> None)
                columns
                (fun party -> None, List.choose TaskAffordances.fromOffer party.Offers)
                reply.Parties

        { region RegionRole.Parties (Some "field.parties.label") (RegionContent.Table table) with
            Actions = List.choose TaskAffordances.fromOffer reply.PartyOffers
            EmptyId = Some "empty.parties"
        }

    let private relatedCaseRegions (reply: CaseReadModel) =
        let source = CaseSelector.Id reply.Snapshot.CaseId

        let action task binding =
            TaskAffordances.offered
                task
                (Some binding)
                Map.empty
                (TaskAffordances.authored task)
                Map.empty

        let columns =
            [
                column
                    "reference"
                    "field.caseReference.label"
                    Prominence.Primary
                    false
                    (fun (row: RelatedCaseRead) ->
                        match row.Case with
                        | Some target -> PresentedValue.Reference target.Reference
                        | None -> PresentedValue.Absent "label.relatedCaseUnavailable")
                column "lifecycle" "input.lifecycle.label" Prominence.Secondary false (fun row ->
                    optional (code Lifecycle.vocabulary) (row.Case |> Option.map _.Lifecycle))
                column "status" "field.status.label" Prominence.Secondary false (fun row ->
                    optional (code CaseStatus.vocabulary) (row.Case |> Option.bind _.Status))
                column "note" "field.linkNote.label" Prominence.Secondary false (fun row ->
                    optional (NoteText.format >> PresentedValue.Narrative) row.Note)
            ]

        let table rows =
            tableWithControls
                (fun (row: RelatedCaseRead) -> LinkId.format row.Id)
                (fun row ->
                    row.Case
                    |> Option.map (fun target ->
                        {
                            Summary = None
                            Highlights = []
                            Sections =
                                [
                                    section
                                        "section.relatedCases.label"
                                        [
                                            value
                                                "reference"
                                                "field.caseReference.label"
                                                Prominence.Detail
                                                (PresentedValue.Reference target.Reference)
                                        ]
                                ]
                        }))
                columns
                (fun row ->
                    row.Case
                    |> Option.map (fun target ->
                        Link.CaseGet
                            {
                                Target = CaseSelector.Id target.CaseId
                            }),
                    [
                        if row.CanRemove then
                            yield
                                action
                                    ClientCompositions.linkRemoval
                                    (Binding.Link(source, row.Id))
                    ])
                rows

        let links = reply.RelatedCases
        let outgoing = table links.Outgoing
        let incoming = table links.Incoming

        [
            if links.CanAdd || not outgoing.Rows.IsEmpty || not incoming.Rows.IsEmpty then
                let group title count (table: RegionTable) =
                    {
                        TitleId = title
                        Count = count
                        RowKeys = List.map (fun (row: RegionRow) -> row.Key) table.Rows
                    }

                let content =
                    { outgoing with
                        Rows = outgoing.Rows @ incoming.Rows
                        Groups =
                            [
                                if not outgoing.Rows.IsEmpty then
                                    group
                                        "section.relatedCases.outgoing"
                                        (int64 outgoing.Rows.Length)
                                        outgoing
                                if not incoming.Rows.IsEmpty then
                                    group
                                        "section.relatedCases.incoming"
                                        links.IncomingCount
                                        incoming
                            ]
                    }

                yield
                    { region
                          RegionRole.RelatedCases
                          (Some "section.relatedCases.label")
                          (RegionContent.Table content) with
                        EmptyId = Some "empty.relatedCases"
                        Actions =
                            [
                                if links.CanAdd then
                                    action ClientCompositions.linkEntry (Binding.LinkNew source)
                            ]
                    }
        ]

    let identity (reply: CaseReadModel) =
        let identitySummary =
            [
                yield! identityFacts reply.Snapshot
                for role, key, label in
                    [
                        PartyRole.Claimant, "claimants", "label.claimants"
                        PartyRole.Insurer, "insurers", "label.insurers"
                    ] do
                    let names =
                        reply.Parties
                        |> List.filter (fun party -> party.Content.Role = role)
                        |> List.choose (fun party ->
                            party.Content.Name |> Option.map PartyName.format)

                    if not names.IsEmpty then
                        yield
                            value
                                key
                                label
                                Prominence.Primary
                                (PresentedValue.Text(String.concat "; " names))

                match reply.Work.Responsible with
                | Some actor ->
                    yield
                        value
                            "responsible"
                            "field.responsibleActor.label"
                            Prominence.Primary
                            (PresentedValue.Actor actor)
                | None -> ()
            ]

        let outcome =
            CaseState.fields reply.Snapshot.State
            |> Option.bind (fun fields -> Handling.closure fields.Handling)
            |> Option.map _.Outcome

        entryRegion
            RegionRole.Identity
            None
            [
                yield
                    value
                        "caseReference"
                        "field.caseReference.label"
                        Prominence.Primary
                        (PresentedValue.Reference reply.Snapshot.Reference)
                if CaseState.lifecycle reply.Snapshot.State <> Lifecycle.Erased then
                    yield
                        value
                            "headline"
                            "label.caseState"
                            Prominence.Primary
                            (headline reply.Headline outcome)

                    if not reply.Attention.IsEmpty then
                        yield
                            value
                                "attention"
                                "label.attention"
                                Prominence.Primary
                                (codes CaseAttention.vocabulary reply.Attention)
                yield! identitySummary
            ]

    let private statusFacts (reply: CaseReadModel) =
        [
            match CaseState.fields reply.Snapshot.State with
            | Some facts ->
                if CaseState.lifecycle reply.Snapshot.State = Lifecycle.Voided then
                    yield
                        value
                            "lifecycle"
                            "input.lifecycle.label"
                            Prominence.Primary
                            (code Lifecycle.vocabulary Lifecycle.Voided)
                else
                    yield
                        value
                            "status"
                            "field.status.label"
                            Prominence.Primary
                            (code CaseStatus.vocabulary facts.Status)

                yield
                    value
                        "progress"
                        "input.progress.label"
                        Prominence.Primary
                        (code CaseProgress.vocabulary (CaseProgress.ofFinancial facts.Finance))

                if reply.Mine then
                    yield
                        value
                            "assignment"
                            "input.assignment.label"
                            Prominence.Secondary
                            (code CaseAssignment.vocabulary CaseAssignment.Mine)
            | None -> ()
        ]

    let view (reply: CaseReadModel) =
        let live = CaseState.lifecycle reply.Snapshot.State <> Lifecycle.Erased
        let groups = caseFacts reply.Snapshot

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
                                    Link = None
                                    Actions = []
                                    Reached = step.Completed
                                    Date = step.Date
                                    Meter =
                                        if step.Stage = CaseStageKind.Paid then
                                            CaseState.fields reply.Snapshot.State
                                            |> Option.bind (fun fields ->
                                                PaymentsScreen.meter fields.Finance)
                                        else
                                            None
                                    StageValue =
                                        step.Outcome
                                        |> Option.map (code ClosureOutcome.vocabulary)
                                        |> Option.orElseWith (fun () ->
                                            step.NetPaid |> Option.map PaymentsScreen.netPaid)
                                        |> Option.orElseWith (fun () ->
                                            step.Amount |> Option.map PresentedValue.money)
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
            |> List.filter (fun column ->
                List.contains column.Key [ "action"; "due"; "kind"; "timing" ])

        let next =
            tableWithControls
                (fun (row: ReminderReadModel) -> ReminderId.format (Reminder.id row.Reminder))
                (fun _ -> None)
                nextColumns
                (fun row ->
                    Some(
                        Link.ReminderGet
                            {
                                Target = CaseSelector.Id(Reminder.caseId row.Reminder)
                                ReminderId = ReminderId.value (Reminder.id row.Reminder)
                            }
                    ),
                    TaskAffordances.reminder row)
                reply.NextActions
            |> ReminderListScreen.recommendCompletion

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
                yield identity reply
                yield!
                    notice
                    |> Option.map (fun notice ->
                        region
                            RegionRole.Notice
                            None
                            (RegionContent.Notice(RegionNotice.single notice)))
                    |> Option.toList
                if live then
                    yield!
                        progress
                        |> Option.map (fun progress ->
                            region
                                RegionRole.Progress
                                (Some "label.stages")
                                (RegionContent.Stages progress))
                        |> Option.toList


                    for group, facts in List.zip scalarGroups groups do
                        if group = RecordGroup.Registration then
                            yield
                                entryRegion
                                    RegionRole.Facts
                                    (Some facts.TitleId)
                                    (statusFacts reply @ facts.Entries)

                    if not reply.Parties.IsEmpty || not reply.PartyOffers.IsEmpty then
                        yield parties reply

                    match CaseState.fields reply.Snapshot.State with
                    | Some fields ->
                        match Handling.closure fields.Handling with
                        | Some closure ->
                            yield
                                entryRegion
                                    RegionRole.Facts
                                    (Some "label.closure")
                                    [
                                        value
                                            "outcome"
                                            "field.outcome.label"
                                            Prominence.Primary
                                            (code ClosureOutcome.vocabulary closure.Outcome)
                                        value
                                            "closingNote"
                                            "field.closingNote.label"
                                            Prominence.Detail
                                            (closure.Note
                                             |> optional (
                                                 NoteText.format >> PresentedValue.Narrative
                                             ))
                                    ]
                        | None -> ()

                        yield
                            region
                                RegionRole.Payments
                                (Some "label.paymentDecisionAndPayments")
                                (RegionContent.Table(
                                    PaymentsScreen.tableWithControls
                                        id
                                        (fun entry ->
                                            None,
                                            [
                                                if
                                                    List.contains
                                                        ClientCompositions.paymentRemoval
                                                        reply.Advice.Acts
                                                then
                                                    yield
                                                        TaskAffordances.paymentRemoveEntry
                                                            reply.Snapshot.CaseId
                                                            reply.Snapshot.Revision
                                                            entry
                                                if
                                                    List.contains
                                                        ClientCompositions.paymentCorrection
                                                        reply.Advice.Acts
                                                then
                                                    yield
                                                        TaskAffordances.paymentCorrectionEntry
                                                            reply.Snapshot.CaseId
                                                            reply.Snapshot.Revision
                                                            entry
                                            ])
                                        fields.Finance
                                        reply.PaymentPayees
                                        reply.LatestPayments
                                ))
                            |> withEmpty "label.noPayments"
                            |> withCount "payments" fields.Finance.PaymentCount
                    | None -> ()

                    yield
                        entryRegion RegionRole.Work (Some "section.work.label") work
                        |> withEmpty "empty.work"

                    yield
                        region
                            RegionRole.NextActions
                            (Some "label.nextActions")
                            (RegionContent.Table next)
                        |> withEmpty "empty.nextActions"

                    yield
                        region
                            RegionRole.Notes
                            (Some "section.notes.label")
                            (RegionContent.Table(
                                NotesScreen.table reply.Snapshot.CaseId reply.Notes
                            ))
                        |> withEmpty "empty.notes"
                        |> withCount "notes" reply.Notes.Count
                yield! relatedCaseRegions reply
                yield entryRegion RegionRole.Details (Some "label.technicalDetails") details
            ]

        |> Screen.withCurrentGroups (
            match reply.Advice.Correction with
            | None -> []
            | Some choices ->
                List.zip scalarGroups groups
                |> List.choose (fun (group, section) ->
                    let offered =
                        match group with
                        | RecordGroup.Registration -> choices.Registration
                        | RecordGroup.Decision -> choices.Decision
                        | RecordGroup.Payment -> []

                    if live && List.contains CorrectionGroupAction.Update offered then
                        Some
                            {
                                CurrentInputGroup.Group = group
                                Section = section
                            }
                    else
                        None)
        )
