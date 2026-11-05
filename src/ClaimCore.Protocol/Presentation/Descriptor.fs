namespace ClaimCore.Protocol.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol

module Descriptor =
    let entries (definitions: EntryDefinition<'reply> list) value =
        definitions
        |> List.map (fun definition ->
            {
                Entry.Key = definition.Key
                LabelId = definition.LabelId
                Prominence = definition.Prominence
                Value = definition.Read value
            })

    let private absent = PresentedValue.Absent "label.notRecorded"

    let private scalar =
        function
        | CaseFieldValue.Reference value -> PresentedValue.Reference value
        | CaseFieldValue.Status value ->
            PresentedValue.Code(Vocabulary.name CaseStatus.vocabulary, CaseStatus.token value)
        | CaseFieldValue.Business(Admitted.Date value) -> PresentedValue.Date value
        | CaseFieldValue.Business(Admitted.Country value) ->
            PresentedValue.Code("country", CountryCode.format value)
        | CaseFieldValue.Business(Admitted.Name value) -> PresentedValue.Party value
        | CaseFieldValue.Business(Admitted.Description value) ->
            PresentedValue.Narrative(IncidentDescription.format value)
        | CaseFieldValue.Business(Admitted.Amount value) -> PresentedValue.Text(Amount.format value)
        | CaseFieldValue.Business(Admitted.Currency value) ->
            PresentedValue.Code("currency", Currency.format value)

    let private fields (declarations: FieldDescriptor list) getFields =
        let paired = declarations |> List.choose _.Pairs |> Set.ofList

        declarations
        |> List.filter (fun field -> not (Set.contains field.Field paired))
        |> List.map (fun field ->
            {
                EntryDefinition.Key = field.Key
                LabelId = FieldPresentation.labelId field.Field
                Prominence = field.Prominence
                IsMoney = field.Pairs.IsSome
                Read =
                    fun reply ->
                        let values = getFields reply

                        let value field =
                            values |> Option.bind (Map.tryFind field >> Option.flatten)

                        match field.Pairs with
                        | Some currency ->
                            match value field.Field, value currency with
                            | Some(CaseFieldValue.Business(Admitted.Amount amount)),
                              Some(CaseFieldValue.Business(Admitted.Currency code)) ->
                                PresentedValue.money (Money.create amount code)
                            | None, None -> absent
                            | _ -> invalidOp "A presented money pair must be complete."
                        | None ->
                            value field.Field |> Option.map scalar |> Option.defaultValue absent
            })

    let caseFields declarations getFields =
        fields (declarations |> List.filter _.Listed) getFields

    let private value key label prominence value =
        {
            Entry.Key = key
            LabelId = label
            Prominence = prominence
            Value = value
        }

    let private optional format =
        Option.map format >> Option.defaultValue absent

    /// A full card uses all declared facts, including non-listed fields; complete money pairs stay atomic.
    let caseFacts (snapshot: CaseSnapshot) =
        match CaseState.fields snapshot.State with
        | None -> []
        | Some facts ->
            let values =
                CaseFields.declarations
                |> List.map (fun field -> field.Field, CaseFields.typedValue field.Field facts)
                |> Map.ofList

            RecordGroup.vocabulary
            |> Vocabulary.all
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

    let caseStages =
        function
        | CaseStageDisplay.Tracker(current, steps) ->
            {
                Section.TitleId = "label.stages"
                Entries =
                    [
                        value
                            "current"
                            "label.currentStage"
                            Prominence.Primary
                            (PresentedValue.Code(
                                Vocabulary.name CaseStageKind.vocabulary,
                                Vocabulary.token CaseStageKind.vocabulary current
                            ))
                    ]
            }
            :: (steps
                |> List.map (fun step ->
                    {
                        Section.TitleId =
                            Vocabulary.messageKey CaseStageKind.vocabulary step.Stage "label"
                        Entries =
                            [
                                value
                                    "completed"
                                    "label.stageCompleted"
                                    Prominence.Primary
                                    (PresentedValue.Code(
                                        Vocabulary.name PresentedBoolean.vocabulary,
                                        Vocabulary.token
                                            PresentedBoolean.vocabulary
                                            (PresentedBoolean.ofBool step.Completed)
                                    ))
                                value
                                    "date"
                                    "label.stageDate"
                                    Prominence.Primary
                                    (optional PresentedValue.Date step.Date)
                                value
                                    "amount"
                                    "label.stageAmount"
                                    Prominence.Primary
                                    (optional PresentedValue.money step.Amount)
                            ]
                    }))
        | CaseStageDisplay.Voided ->
            [
                {
                    Section.TitleId = "label.stages"
                    Entries =
                        [
                            value
                                "state"
                                "field.status.label"
                                Prominence.Primary
                                (PresentedValue.State CaseHeadline.Voided)
                        ]
                }
            ]
        | CaseStageDisplay.Erased ->
            [
                {
                    Section.TitleId = "label.stages"
                    Entries =
                        [
                            value
                                "state"
                                "field.status.label"
                                Prominence.Primary
                                (PresentedValue.State CaseHeadline.Erased)
                        ]
                }
            ]
        | CaseStageDisplay.Reinstated ->
            [
                {
                    Section.TitleId = "label.stages"
                    Entries =
                        [
                            value
                                "state"
                                "field.status.label"
                                Prominence.Primary
                                (PresentedValue.Message "label.stageReinstated")
                        ]
                }
            ]

    let caseView (reply: CaseReadModel) =
        [
            yield
                {
                    TitleId = "subject.CASE.label"
                    Entries =
                        [
                            value
                                "caseReference"
                                "field.caseReference.label"
                                Prominence.Primary
                                (PresentedValue.Reference reply.Snapshot.Reference)
                            value
                                "headline"
                                "field.status.label"
                                Prominence.Primary
                                (PresentedValue.State reply.Headline)
                            value
                                "revision"
                                "label.revisionLabel"
                                Prominence.Detail
                                (PresentedValue.Identifier(Revision.format reply.Snapshot.Revision))
                        ]
                }
            if CaseState.lifecycle reply.Snapshot.State <> Lifecycle.Erased then
                yield
                    {
                        TitleId = "label.attention"
                        Entries =
                            reply.Attention
                            |> List.map (fun attention ->
                                let token = Vocabulary.token CaseAttention.vocabulary attention

                                value
                                    token
                                    "label.attention"
                                    Prominence.Primary
                                    (PresentedValue.Code(
                                        Vocabulary.name CaseAttention.vocabulary,
                                        token
                                    )))
                    }

                match reply.Advice.Recommended with
                | Some act ->
                    yield
                        {
                            TitleId = "label.recommendedNext"
                            Entries =
                                [
                                    value
                                        "recommended"
                                        "label.recommendedNext"
                                        Prominence.Primary
                                        (PresentedValue.Code("act", CaseActKind.token act))
                                ]
                        }
                | None -> ()
            yield! caseFacts reply.Snapshot
            if CaseState.lifecycle reply.Snapshot.State <> Lifecycle.Erased then
                yield! caseStages reply.Stages
            if CaseState.lifecycle reply.Snapshot.State <> Lifecycle.Erased then
                yield caseWork reply.Work
        ]

    let reminder (reply: ReminderReadModel) =
        let current = reply.Reminder

        [
            {
                TitleId = "label.reminder"
                Entries =
                    [
                        yield
                            value
                                "caseReference"
                                "field.caseReference.label"
                                Prominence.Primary
                                (PresentedValue.Reference reply.Reference)
                        yield
                            value
                                "reminderId"
                                "field.reminderId.label"
                                Prominence.Detail
                                (PresentedValue.Identifier(ReminderId.format (Reminder.id current)))
                        yield
                            value
                                "revision"
                                "label.reminderRevision"
                                Prominence.Detail
                                (PresentedValue.Identifier(
                                    Revision.format (Reminder.revision current)
                                ))
                        yield
                            value
                                "action"
                                "field.reminderAction.label"
                                Prominence.Primary
                                (PresentedValue.Text(
                                    ReminderAction.format (Reminder.action current)
                                ))
                        yield
                            value
                                "dueDate"
                                "field.reminderDueDate.label"
                                Prominence.Primary
                                (PresentedValue.Date(Reminder.due current))
                        yield
                            value
                                "state"
                                "input.reminderState.label"
                                Prominence.Primary
                                (PresentedValue.Code(
                                    Vocabulary.name ReminderState.vocabulary,
                                    ReminderState.token (Reminder.state current)
                                ))
                        match reply.Timing with
                        | Some timing ->
                            yield
                                value
                                    "timing"
                                    "input.reminderDue.label"
                                    Prominence.Secondary
                                    (PresentedValue.Code(
                                        Vocabulary.name ReminderDue.vocabulary,
                                        Vocabulary.token ReminderDue.vocabulary timing
                                    ))
                        | None -> ()
                        if not reply.Active then
                            yield
                                value
                                    "parentState"
                                    "field.status.label"
                                    Prominence.Secondary
                                    (PresentedValue.Code(
                                        Vocabulary.name CaseDue.vocabulary,
                                        Vocabulary.token CaseDue.vocabulary CaseDue.Inactive
                                    ))
                        yield
                            value
                                "recordedAt"
                                "label.recordedAt"
                                Prominence.Detail
                                (PresentedValue.Instant reply.RecordedAt)
                        yield
                            value
                                "recordedBy"
                                "label.recordedBy"
                                Prominence.Secondary
                                (PresentedValue.Actor reply.RecordedBy)
                    ]
            }
        ]

    let caseSummaryColumns =
        [
            {
                Key = "caseReference"
                LabelId = FieldPresentation.labelId BusinessField.CaseReference
                Prominence = Prominence.Primary
                IsMoney = false
                Read = fun (row: CaseSummary) -> PresentedValue.Reference row.Reference
            }
            {
                Key = "headline"
                LabelId = FieldPresentation.labelId BusinessField.Status
                Prominence = Prominence.Primary
                IsMoney = false
                Read = fun row -> PresentedValue.State row.Headline
            }
            yield!
                caseFields CaseFields.declarations (fun (row: CaseSummary) ->
                    CaseState.fields row.State
                    |> Option.map (fun (facts: CaseSummaryFacts) -> facts.Fields))
                |> List.map (fun definition ->
                    { definition with
                        Read =
                            fun row ->
                                if CaseState.lifecycle row.State = Lifecycle.Erased then
                                    PresentedValue.Absent "lifecycle.ERASED.label"
                                else
                                    definition.Read row
                    })
            {
                Key = "responsible"
                LabelId = FieldPresentation.labelId BusinessField.ResponsibleActor
                Prominence = Prominence.Secondary
                IsMoney = false
                Read =
                    fun (row: CaseSummary) ->
                        row.Responsible
                        |> Option.map PresentedValue.Actor
                        |> Option.defaultValue (PresentedValue.Absent "view.UNASSIGNED.label")
            }
            {
                Key = "nextAction"
                LabelId = "field.reminderAction.label"
                Prominence = Prominence.Secondary
                IsMoney = false
                Read =
                    fun row ->
                        row.NextAction
                        |> Option.map (fun next ->
                            PresentedValue.Text(ReminderAction.format next.Action))
                        |> Option.defaultValue (PresentedValue.Absent "view.NO_NEXT_ACTION.label")
            }
            {
                Key = "changedAt"
                LabelId = "label.changedAt"
                Prominence = Prominence.Detail
                IsMoney = false
                Read = fun row -> PresentedValue.Instant row.ChangedAt
            }
        ]

    let caseSummary row =
        [
            {
                TitleId = "subject.CASE.label"
                Entries = entries caseSummaryColumns row
            }
        ]

    let private count key label value =
        {
            Entry.Key = key
            LabelId = label
            Prominence = Prominence.Primary
            Value = PresentedValue.Count value
        }

    /// Counts retain known zero values and the producer's order; absence is never inferred.
    let caseViews (reply: CaseViews) =
        reply.Items
        |> List.map (fun item ->
            let token = CaseView.token item.View
            count token ("view." + token + ".label") item.Count)

    let reminderViews (reply: ReminderViews) =
        reply.Items
        |> List.map (fun item ->
            let token = ReminderView.token item.View
            count token ("view." + token + ".label") item.Count)

    let workOverview (reply: WorkOverview) =
        [
            count "actionableCases" "label.actionableCases" reply.ActionableCases
            count "missingPlanCases" "label.missingPlanCases" reply.MissingPlanCases
            count "overdueCases" "label.overdueCases" reply.OverdueCases
            count "overdueReminders" "label.overdueReminders" reply.OverdueReminders
            count "unassignedCases" "label.unassignedCases" reply.UnassignedCases
            count
                "unavailableAssigneeCases"
                "label.unavailableAssigneeCases"
                reply.UnavailableAssigneeCases
            count "attentionCases" "label.attentionCases" reply.AttentionCases
        ]

    let private difference key label format (change: ValueChange<'value>) =
        {
            TitleId = label
            Entries =
                [
                    value
                        (key + ".before")
                        "label.before"
                        Prominence.Secondary
                        (optional format change.Before)
                    value
                        (key + ".after")
                        "label.after"
                        Prominence.Primary
                        (optional format change.After)
                ]
        }

    let private reminderDifference (change: ReminderDifference) =
        let prefix = "reminder." + ReminderId.format change.ReminderId + "."

        [
            match change.Action with
            | Some change ->
                yield
                    difference
                        (prefix + "action")
                        "field.reminderAction.label"
                        (ReminderAction.format >> PresentedValue.Text)
                        change
            | None -> ()
            match change.Due with
            | Some change ->
                yield
                    difference
                        (prefix + "dueDate")
                        "field.reminderDueDate.label"
                        PresentedValue.Date
                        change
            | None -> ()
            match change.State with
            | Some change ->
                yield
                    difference
                        (prefix + "state")
                        "input.reminderState.label"
                        (fun state ->
                            PresentedValue.Code(
                                Vocabulary.name ReminderState.vocabulary,
                                ReminderState.token state
                            ))
                        change
            | None -> ()
        ]

    /// Differences are supplied by the authorized core snapshot, never reconstructed by a renderer.
    let timelineChange =
        function
        | TimelineChange.CaseField change ->
            [
                difference
                    (BusinessField.key change.FieldName)
                    (FieldPresentation.labelId change.FieldName)
                    scalar
                    {
                        Before = change.Before
                        After = change.After
                    }
            ]
        | TimelineChange.CaseMoney change ->
            [
                difference
                    (BusinessField.key change.FieldName)
                    (FieldPresentation.labelId change.FieldName)
                    PresentedValue.money
                    change.Change
            ]
        | TimelineChange.Lifecycle change ->
            [
                difference
                    "lifecycle"
                    "input.lifecycle.label"
                    (fun lifecycle ->
                        PresentedValue.Code(
                            Vocabulary.name Lifecycle.vocabulary,
                            Lifecycle.token lifecycle
                        ))
                    {
                        Before = change.Before
                        After = Some change.After
                    }
            ]
        | TimelineChange.Responsibility change ->
            [
                difference
                    "responsibleActor"
                    "field.responsibleActor.label"
                    PresentedValue.Actor
                    change
            ]
        | TimelineChange.HandlingSummary change ->
            [
                difference
                    "handlingSummary"
                    "field.handlingSummary.label"
                    (HandlingSummary.format >> PresentedValue.Narrative)
                    change
            ]
        | TimelineChange.Reminder change -> reminderDifference change

    let timelineEntry (entry: CaseTimelineEntry) =
        let act, revision, identifiers =
            match entry.Subject with
            | TimelineSubject.CaseFact(revision, act) ->
                PresentedValue.Code(Vocabulary.name CaseActKind.vocabulary, CaseActKind.token act),
                revision,
                []
            | TimelineSubject.Work(revision, act) ->
                PresentedValue.Code(Vocabulary.name WorkActKind.vocabulary, WorkActKind.token act),
                revision,
                []
            | TimelineSubject.Reminder(act, id, revision, next) ->
                let identifiers =
                    [
                        yield
                            value
                                "reminderId"
                                "field.reminderId.label"
                                Prominence.Detail
                                (PresentedValue.Identifier(ReminderId.format id))
                        match next with
                        | Some(id, revision) ->
                            yield
                                value
                                    "nextReminderId"
                                    "field.reminderId.label"
                                    Prominence.Detail
                                    (PresentedValue.Identifier(ReminderId.format id))

                            yield
                                value
                                    "nextReminderRevision"
                                    "label.reminderRevision"
                                    Prominence.Detail
                                    (PresentedValue.Identifier(Revision.format revision))
                        | None -> ()
                    ]

                PresentedValue.Code(
                    Vocabulary.name ReminderActKind.vocabulary,
                    ReminderActKind.token act
                ),
                revision,
                identifiers

        [
            yield
                {
                    TitleId = "label.changes"
                    Entries =
                        [
                            yield
                                value
                                    "recordedAt"
                                    "label.recordedAt"
                                    Prominence.Primary
                                    (PresentedValue.Instant entry.RecordedAt)
                            yield
                                value
                                    "recordedBy"
                                    "label.recordedBy"
                                    Prominence.Primary
                                    (PresentedValue.Actor entry.RecordedBy)
                            yield value "act" "label.action" Prominence.Primary act
                            yield
                                value
                                    "revision"
                                    "label.revisionLabel"
                                    Prominence.Detail
                                    (PresentedValue.Identifier(Revision.format revision))
                            yield
                                value
                                    "operationId"
                                    "field.operationId.label"
                                    Prominence.Detail
                                    (PresentedValue.Identifier(OperationId.format entry.OperationId))
                            yield! identifiers
                        ]
                }
            match entry.Changes with
            | TimelineChanges.Disclosed changes ->
                yield! changes.Items |> List.collect timelineChange
            | TimelineChanges.Withheld ->
                yield
                    {
                        TitleId = "label.changes"
                        Entries =
                            [
                                value
                                    "changes"
                                    "label.changes"
                                    Prominence.Primary
                                    (PresentedValue.Absent "label.changesWithheld")
                            ]
                    }
            | TimelineChanges.Erased ->
                yield
                    {
                        TitleId = "label.changes"
                        Entries =
                            [
                                value
                                    "changes"
                                    "label.changes"
                                    Prominence.Primary
                                    (PresentedValue.Absent "label.changesErased")
                            ]
                    }
        ]


    let private code vocabulary item =
        PresentedValue.Code(Vocabulary.name vocabulary, Vocabulary.token vocabulary item)

    let private codes vocabulary items =
        PresentedValue.Codes(
            Vocabulary.name vocabulary,
            items |> List.map (Vocabulary.token vocabulary)
        )

    let private boolean item =
        code PresentedBoolean.vocabulary (PresentedBoolean.ofBool item)

    let private column key label prominence isMoney read =
        {
            EntryDefinition.Key = key
            LabelId = label
            Prominence = prominence
            IsMoney = isMoney
            Read = read
        }

    let private section title entries =
        {
            Section.TitleId = title
            Entries = entries
        }

    let reminderColumns =
        [
            column
                "reference"
                "field.caseReference.label"
                Prominence.Primary
                false
                (fun (item: ReminderReadModel) -> PresentedValue.Reference item.Reference)
            column "action" "field.reminderAction.label" Prominence.Primary false (fun item ->
                PresentedValue.Text(ReminderAction.format (Reminder.action item.Reminder)))
            column "due" "field.reminderDueDate.label" Prominence.Primary false (fun item ->
                PresentedValue.Date(Reminder.due item.Reminder))
            column "state" "input.reminderState.label" Prominence.Secondary false (fun item ->
                code ReminderState.vocabulary (Reminder.state item.Reminder))
            column "timing" "input.reminderDue.label" Prominence.Primary false (fun item ->
                if not item.Active then
                    code CaseDue.vocabulary CaseDue.Inactive
                else
                    optional (code ReminderDue.vocabulary) item.Timing)
            column "recordedBy" "label.recordedBy" Prominence.Detail false (fun item ->
                PresentedValue.Actor item.RecordedBy)
            column "recordedAt" "label.recordedAt" Prominence.Detail false (fun item ->
                PresentedValue.Instant item.RecordedAt)
            column "id" "field.reminderId.label" Prominence.Detail false (fun item ->
                PresentedValue.Identifier(ReminderId.format (Reminder.id item.Reminder)))
        ]

    let timelineColumns =
        [
            column
                "recordedBy"
                "label.recordedBy"
                Prominence.Primary
                false
                (fun (item: CaseTimelineEntry) -> PresentedValue.Actor item.RecordedBy)
            column "act" "label.action" Prominence.Primary false (fun item ->
                match item.Subject with
                | TimelineSubject.CaseFact(_, act) -> code CaseActKind.vocabulary act
                | TimelineSubject.Work(_, act) -> code WorkActKind.vocabulary act
                | TimelineSubject.Reminder(act, _, _, _) -> code ReminderActKind.vocabulary act)
            column "recordedAt" "label.recordedAt" Prominence.Secondary false (fun item ->
                PresentedValue.Instant item.RecordedAt)
            column "sequence" "label.sequence" Prominence.Detail false (fun item ->
                PresentedValue.Identifier(Sequence.format item.Position.Sequence))
            column "operationId" "field.operationId.label" Prominence.Detail false (fun item ->
                PresentedValue.Identifier(OperationId.format item.OperationId))
        ]

    let assigneeColumns =
        [
            column
                "actor"
                "field.profile.displayName.label"
                Prominence.Primary
                false
                PresentedValue.Actor
            column "actorId" "input.actorId.label" Prominence.Detail false (fun actor ->
                PresentedValue.Identifier(ActorId.format actor.ActorId))
        ]

    let actorColumns =
        [
            column
                "actor"
                "field.profile.displayName.label"
                Prominence.Primary
                false
                (fun (item: ActorReviewItem) -> PresentedValue.Actor item.Actor)
            column "state" "label.accountState" Prominence.Primary false (fun item ->
                code AccountState.vocabulary item.State)
            column "roles" "field.role.label" Prominence.Secondary false (fun item ->
                codes Role.vocabulary (item.Roles |> List.map _.Role))
            column
                "responsibleCases"
                "label.responsibleCases"
                Prominence.Secondary
                false
                (fun item ->
                    item.ResponsibleCases
                    |> Option.map (fun (count: ActorResponsibleCases) ->
                        PresentedValue.Count count.Count)
                    |> Option.defaultValue (PresentedValue.Absent "label.detailsWithheld"))
            column "contact" "field.profile.contact.label" Prominence.Detail false (fun item ->
                optional (ContactText.format >> PresentedValue.Text) item.Contact)
            column "actorId" "input.actorId.label" Prominence.Detail false (fun item ->
                PresentedValue.Identifier(ActorId.format item.Actor.ActorId))
            column "principalKind" "field.principal.kind.label" Prominence.Detail false (fun item ->
                let kind, _, _ = PrincipalKey.parts item.Principal in
                code PrincipalKind.vocabulary kind)
            column "issuer" "field.principal.issuer.label" Prominence.Detail false (fun item ->
                let _, issuer, _ = PrincipalKey.parts item.Principal in
                PresentedValue.Identifier issuer)
            column "subject" "field.principal.subject.label" Prominence.Detail false (fun item ->
                let _, _, subject = PrincipalKey.parts item.Principal in
                PresentedValue.Identifier subject)
        ]

    let private roleSections (roles: ActorRoleSummary list) =
        roles
        |> List.map (fun role ->
            section
                "field.role.label"
                [
                    value
                        "role"
                        "field.role.label"
                        Prominence.Primary
                        (code Role.vocabulary role.Role)
                    value
                        "installation"
                        "label.installationRoleAssigned"
                        Prominence.Secondary
                        (boolean role.Installation)
                    value
                        "cases"
                        "label.caseRoleAssignments"
                        Prominence.Secondary
                        (PresentedValue.Count role.CaseCount)
                ])

    let actor item =
        section "label.account" (entries actorColumns item) :: roleSections item.Roles

    let grantColumns =
        [
            column "role" "field.role.label" Prominence.Primary false (fun (grant: ScopedGrant) ->
                code Role.vocabulary grant.Role)
            column "scope" "input.scope.label" Prominence.Primary false (fun grant ->
                code
                    ScopeKind.vocabulary
                    (match grant.Scope with
                     | GrantScope.Installation -> ScopeKind.Installation
                     | GrantScope.Case _ -> ScopeKind.Case))
            column "reference" "field.caseReference.label" Prominence.Primary false (fun grant ->
                match grant.Scope with
                | GrantScope.Case(_, reference) -> PresentedValue.Reference reference
                | GrantScope.Installation -> absent)
        ]

    let grant item = entries grantColumns item

    let authorityHistoryColumns =
        [
            column
                "recordedAt"
                "label.recordedAt"
                Prominence.Secondary
                false
                (fun (item: AuthorityHistoryEntry) -> PresentedValue.Instant item.RecordedAt)
            column "act" "label.action" Prominence.Primary false (fun item ->
                code AuthorityKind.vocabulary (AuthorityHistoryFacts.kind item.Facts))
            column "recordedBy" "label.recordedBy" Prominence.Primary false (fun item ->
                PresentedValue.Actor item.RecordedBy)
            column "target" "label.targetAccount" Prominence.Primary false (fun item ->
                PresentedValue.Actor item.Target)
            column "details" "label.details" Prominence.Primary false (fun item ->
                match item.Facts with
                | AuthorityHistoryFacts.Granted grant
                | AuthorityHistoryFacts.Revoked grant -> code Role.vocabulary grant.Role
                | AuthorityHistoryFacts.ProfileSet(Some profile) ->
                    PresentedValue.Party profile.DisplayName
                | AuthorityHistoryFacts.ProfileSet None
                | AuthorityHistoryFacts.ProfileErased ->
                    PresentedValue.Absent "label.changesErased"
                | AuthorityHistoryFacts.OwnerProvisioned principal
                | AuthorityHistoryFacts.Registered principal ->
                    let kind, _, _ = PrincipalKey.parts principal in
                    code PrincipalKind.vocabulary kind
                | AuthorityHistoryFacts.Enabled -> boolean true
                | AuthorityHistoryFacts.Disabled -> boolean false)
            column "scope" "input.scope.label" Prominence.Secondary false (fun item ->
                match item.Facts with
                | AuthorityHistoryFacts.Granted grant
                | AuthorityHistoryFacts.Revoked grant ->
                    code
                        ScopeKind.vocabulary
                        (match grant.Scope with
                         | GrantScope.Installation -> ScopeKind.Installation
                         | GrantScope.Case _ -> ScopeKind.Case)
                | _ -> PresentedValue.Absent "label.notApplicable")
            column "reference" "field.caseReference.label" Prominence.Secondary false (fun item ->
                match item.Facts with
                | AuthorityHistoryFacts.Granted {
                                                    Scope = GrantScope.Case(_, reference)
                                                }
                | AuthorityHistoryFacts.Revoked {
                                                    Scope = GrantScope.Case(_, reference)
                                                } -> PresentedValue.Reference reference
                | _ -> PresentedValue.Absent "label.notApplicable")
            column "sequence" "label.sequence" Prominence.Detail false (fun item ->
                PresentedValue.Identifier(Sequence.format item.Sequence))
        ]

    let authorityHistory item =
        let facts =
            match item.Facts with
            | AuthorityHistoryFacts.OwnerProvisioned principal
            | AuthorityHistoryFacts.Registered principal ->
                let kind, issuer, subject = PrincipalKey.parts principal

                [
                    value
                        "principalKind"
                        "field.principal.kind.label"
                        Prominence.Detail
                        (code PrincipalKind.vocabulary kind)
                    value
                        "issuer"
                        "field.principal.issuer.label"
                        Prominence.Detail
                        (PresentedValue.Identifier issuer)
                    value
                        "subject"
                        "field.principal.subject.label"
                        Prominence.Detail
                        (PresentedValue.Identifier subject)
                ]
            | AuthorityHistoryFacts.Granted item
            | AuthorityHistoryFacts.Revoked item -> grant item
            | AuthorityHistoryFacts.ProfileSet profile ->
                [
                    value
                        "name"
                        "field.profile.displayName.label"
                        Prominence.Primary
                        (optional
                            (fun (profile: ActorProfile) ->
                                PresentedValue.Party profile.DisplayName)
                            profile)
                    value
                        "contact"
                        "field.profile.contact.label"
                        Prominence.Secondary
                        (profile
                         |> Option.bind _.Contact
                         |> optional (ContactText.format >> PresentedValue.Text))
                ]
            | AuthorityHistoryFacts.Enabled
            | AuthorityHistoryFacts.Disabled
            | AuthorityHistoryFacts.ProfileErased -> []

        [
            section "label.authorityHistory" (entries authorityHistoryColumns item @ facts)
        ]

    let private claims (item: RequesterClaims) =
        [
            value
                "nameHint"
                "label.requestedName"
                Prominence.Primary
                (optional PresentedValue.Party item.Name)
            value
                "usernameHint"
                "label.requestedUsername"
                Prominence.Secondary
                (optional PresentedValue.Party item.PreferredUsername)
            value
                "emailHint"
                "label.requestedEmail"
                Prominence.Secondary
                (optional PresentedValue.Party item.Email)
        ]

    let accessRequest (item: AccessRequest) =
        let kind, issuer, subject = PrincipalKey.parts item.Principal

        [
            section
                "label.accessRequest"
                (claims item.Claims
                 @ [
                     value
                         "principalKind"
                         "field.principal.kind.label"
                         Prominence.Secondary
                         (code PrincipalKind.vocabulary kind)
                     value
                         "issuer"
                         "field.principal.issuer.label"
                         Prominence.Detail
                         (PresentedValue.Identifier issuer)
                     value
                         "subject"
                         "field.principal.subject.label"
                         Prominence.Detail
                         (PresentedValue.Identifier subject)
                     value
                         "firstSeen"
                         "label.firstSeen"
                         Prominence.Secondary
                         (PresentedValue.Instant item.FirstSeen)
                     value
                         "lastSeen"
                         "label.lastSeen"
                         Prominence.Secondary
                         (PresentedValue.Instant item.LastSeen)
                 ])
        ]

    let context (reply: ContextReply) =
        let state, details =
            match reply.Requester with
            | RequesterState.Active item ->
                RequesterKind.Active,
                [
                    value "self" "label.account" Prominence.Primary (PresentedValue.Actor item.Self)
                    value
                        "capabilities"
                        "label.capabilities"
                        Prominence.Primary
                        (codes Capability.vocabulary item.Capability)
                    value
                        "views"
                        "label.views"
                        Prominence.Secondary
                        (codes View.vocabulary item.View)
                    value
                        "issuer"
                        "field.principal.issuer.label"
                        Prominence.Detail
                        (PresentedValue.Identifier item.Issuer)
                    value
                        "installationId"
                        "label.installationId"
                        Prominence.Detail
                        (PresentedValue.Identifier(item.InstallationId.ToString("D")))
                    value
                        "productVersion"
                        "label.productVersion"
                        Prominence.Detail
                        (PresentedValue.Identifier item.Runtime.ProductVersion)
                ]
            | RequesterState.NoRoles item ->
                RequesterKind.NoRoles,
                [
                    value "self" "label.account" Prominence.Primary (PresentedValue.Actor item.Self)
                    value
                        "installationId"
                        "label.installationId"
                        Prominence.Detail
                        (PresentedValue.Identifier(item.InstallationId.ToString("D")))
                    value
                        "businessDate"
                        "label.businessDate"
                        Prominence.Secondary
                        (PresentedValue.Date(CalendarDate.ofDate item.BusinessDate))
                    value
                        "timeZone"
                        "label.timeZone"
                        Prominence.Secondary
                        (PresentedValue.Text item.TimeZone)
                ]
            | RequesterState.Disabled item ->
                RequesterKind.Disabled,
                [ value "self" "label.account" Prominence.Primary (PresentedValue.Actor item) ]
            | RequesterState.NotRegistered item ->
                RequesterKind.NotRegistered,
                claims item.Claims
                @ [
                    value
                        "issuer"
                        "field.principal.issuer.label"
                        Prominence.Detail
                        (PresentedValue.Identifier item.Issuer)
                    value
                        "subject"
                        "field.principal.subject.label"
                        Prominence.Detail
                        (PresentedValue.Identifier item.Subject)
                ]

        [
            section
                "label.account"
                ([
                    value
                        "state"
                        "label.accountState"
                        Prominence.Primary
                        (code RequesterKind.vocabulary state)
                    value
                        "guidance"
                        "label.account"
                        Prominence.Primary
                        (PresentedValue.Message(
                            Vocabulary.messageKey RequesterKind.vocabulary state "meaning"
                        ))
                 ]
                 @ details)
        ]
        @ (match reply.Requester with
           | RequesterState.Active item -> roleSections item.Roles
           | RequesterState.NoRoles _
           | RequesterState.Disabled _
           | RequesterState.NotRegistered _ -> [])

    let caseTotals (reply: CaseWorklistSummary) =
        let group title vocabulary (items: ValueCount<'value> list) =
            section
                title
                (items
                 |> List.map (fun item ->
                     value
                         (Vocabulary.token vocabulary item.Value)
                         (Vocabulary.messageKey vocabulary item.Value "label")
                         Prominence.Primary
                         (PresentedValue.Count item.Count)))

        let totals title (items: CurrencyTotal list) =
            section
                title
                (items
                 |> List.map (fun item ->
                     value
                         (Currency.format item.Currency)
                         "label.total"
                         Prominence.Primary
                         (PresentedValue.moneyTotal item.Currency item.Amount)))

        [
            section
                "label.cases"
                [
                    value
                        "total"
                        "label.total"
                        Prominence.Primary
                        (PresentedValue.Count reply.Counts.Total)
                ]
            group "input.assignment.label" CaseAssignment.vocabulary reply.Counts.Assignment
            group "input.due.label" CaseDue.vocabulary reply.Counts.Due
            group "input.lifecycle.label" Lifecycle.vocabulary reply.Counts.Lifecycles
            group "input.status.label" CaseStatus.vocabulary reply.Counts.Statuses
            group "input.progress.label" CaseProgress.vocabulary reply.Counts.Progress
            totals "label.claimedTotals" reply.Claimed
            totals "label.payableTotals" reply.Payable
        ]


    let private flatten sections =
        sections |> List.collect (fun (section: Section) -> section.Entries)

    let private caseReviewPresentation (review: AdvisoryReview) =
        let declarations =
            CaseFields.declarations
            |> List.filter (fun field -> field.Section <> Some CaseSection.Work)

        let snapshotValues (snapshot: CaseSnapshot option) =
            declarations
            |> List.map (fun field ->
                field.Field,
                (match snapshot with
                 | Some snapshot when field.Field = BusinessField.CaseReference ->
                     Some(CaseFieldValue.Reference snapshot.Reference)
                 | Some snapshot ->
                     CaseState.fields snapshot.State
                     |> Option.bind (CaseFields.typedValue field.Field)
                 | None -> None))
            |> Map.ofList

        let sides get snapshot =
            review.Changes.Fields
            |> List.fold
                (fun values change -> Map.add change.FieldName (get change) values)
                (snapshotValues snapshot)

        let definitions = fields declarations (fun values -> Some values)
        let before = entries definitions (sides _.Before review.Before)
        let after = entries definitions (sides _.After (Some review.Proposed))

        let changedKeys =
            review.Changes.Fields
            |> List.map (fun change ->
                declarations
                |> List.tryFind (fun field -> field.Pairs = Some change.FieldName)
                |> Option.map _.Field
                |> Option.defaultValue change.FieldName
                |> BusinessField.key)
            |> Set.ofList

        let erased = CaseState.lifecycle review.Proposed.State = Lifecycle.Erased

        let changed =
            List.zip before after
            |> List.choose (fun (before, after) ->
                if Set.contains after.Key changedKeys then
                    Some
                        {
                            PresentedChange.Key = after.Key
                            LabelId = after.LabelId
                            Before = before.Value
                            After =
                                if erased && after.Value = absent then
                                    PresentedValue.Absent "lifecycle.ERASED.label"
                                else
                                    after.Value
                        }
                else
                    None)

        let lifecycle =
            review.Changes.Lifecycle
            |> Option.toList
            |> List.map (fun change ->
                {
                    PresentedChange.Key = "lifecycle"
                    LabelId = "input.lifecycle.label"
                    Before = optional (code Lifecycle.vocabulary) change.Before
                    After = code Lifecycle.vocabulary change.After
                })

        let unchanged =
            if review.Before.IsNone || erased then
                []
            else
                (after |> List.filter (fun entry -> not (Set.contains entry.Key changedKeys)))
                @ (if review.Changes.Lifecycle.IsNone then
                       [
                           value
                               "lifecycle"
                               "input.lifecycle.label"
                               Prominence.Primary
                               (code
                                   Lifecycle.vocabulary
                                   (CaseState.lifecycle review.Proposed.State))
                       ]
                   else
                       [])

        lifecycle @ changed, unchanged

    let private workEntries (snapshot: CaseWorkReadModel) =
        [
            value
                "responsible"
                "field.responsibleActor.label"
                Prominence.Primary
                (optional PresentedValue.Actor snapshot.Responsible)
            value
                "handlingSummary"
                "field.handlingSummary.label"
                Prominence.Primary
                (optional (HandlingSummary.format >> PresentedValue.Narrative) snapshot.Summary)
        ]

    let private reminderEntries (reminder: Reminder) =
        let id = ReminderId.format (Reminder.id reminder)

        [
            value
                (id + ".id")
                "field.reminderId.label"
                Prominence.Detail
                (PresentedValue.Identifier id)
            value
                (id + ".action")
                "field.reminderAction.label"
                Prominence.Primary
                (PresentedValue.Text(ReminderAction.format (Reminder.action reminder)))
            value
                (id + ".due")
                "field.reminderDueDate.label"
                Prominence.Primary
                (PresentedValue.Date(Reminder.due reminder))
            value
                (id + ".state")
                "input.reminderState.label"
                Prominence.Primary
                (code ReminderState.vocabulary (Reminder.state reminder))
        ]

    let private profileEntries (profile: ActorProfile option) =
        [
            value
                "profileName"
                "field.profile.displayName.label"
                Prominence.Primary
                (optional
                    (fun (profile: ActorProfile) -> PresentedValue.Party profile.DisplayName)
                    profile)
            value
                "profileContact"
                "field.profile.contact.label"
                Prominence.Primary
                (profile
                 |> Option.bind _.Contact
                 |> optional (ContactText.format >> PresentedValue.Text))
        ]

    let private authorityStateEntries (state: AuthorityState) =
        [ value "enabled" "label.enabled" Prominence.Primary (boolean state.Enabled) ]
        @ profileEntries state.Profile
        @ (state.Grants
           |> List.collect (fun item ->
               let identity =
                   Role.token item.Role
                   + "."
                   + (match item.Scope with
                      | GrantScope.Installation -> "installation"
                      | GrantScope.Case(id, _) -> CaseId.format id)

               grant item
               |> List.map (fun entry ->
                   { entry with
                       Key = identity + "." + entry.Key
                   })))

    let private authorityEffects (effects: AuthorityReviewEffect list) =
        effects
        |> List.map (fun effect ->
            let kind, details =
                match effect with
                | AuthorityReviewEffect.Register principal
                | AuthorityReviewEffect.ProvisionOwner principal ->
                    let kind, issuer, subject = PrincipalKey.parts principal

                    (match effect with
                     | AuthorityReviewEffect.Register _ -> AuthorityKind.Register
                     | _ -> AuthorityKind.ProvisionOwner),
                    [
                        value
                            "principalKind"
                            "field.principal.kind.label"
                            Prominence.Detail
                            (code PrincipalKind.vocabulary kind)
                        value
                            "issuer"
                            "field.principal.issuer.label"
                            Prominence.Detail
                            (PresentedValue.Identifier issuer)
                        value
                            "subject"
                            "field.principal.subject.label"
                            Prominence.Detail
                            (PresentedValue.Identifier subject)
                    ]
                | AuthorityReviewEffect.SetProfile _ -> AuthorityKind.SetProfile, []
                | AuthorityReviewEffect.EraseProfile -> AuthorityKind.EraseProfile, []
                | AuthorityReviewEffect.Grant item -> AuthorityKind.Grant, grant item
                | AuthorityReviewEffect.Revoke item -> AuthorityKind.Revoke, grant item
                | AuthorityReviewEffect.Enable -> AuthorityKind.Enable, []
                | AuthorityReviewEffect.Disable -> AuthorityKind.Disable, []

            section
                "label.effect"
                (value "act" "label.action" Prominence.Primary (code AuthorityKind.vocabulary kind)
                 :: details))

    let private sameValue before after =
        match before, after with
        | PresentedValue.Actor before, PresentedValue.Actor after -> before.ActorId = after.ActorId
        | _ -> before = after

    let private changes afterMissing before after =
        let previous =
            before |> List.map (fun (entry: Entry) -> entry.Key, entry) |> Map.ofList

        let proposed =
            after |> List.map (fun (entry: Entry) -> entry.Key, entry) |> Map.ofList

        let keys = (after @ before) |> List.map _.Key |> List.distinct

        keys
        |> List.fold
            (fun (changed, unchanged) key ->
                let old = Map.tryFind key previous
                let next = Map.tryFind key proposed
                let definition = next |> Option.orElse old |> Option.get
                let oldValue = old |> Option.map _.Value |> Option.defaultValue absent
                let newValue = next |> Option.map _.Value |> Option.defaultValue afterMissing

                if sameValue oldValue newValue then
                    changed,
                    (if old.IsSome && next.IsSome then
                         definition :: unchanged
                     else
                         unchanged)
                else
                    {
                        PresentedChange.Key = key
                        LabelId = definition.LabelId
                        Before = oldValue
                        After = newValue
                    }
                    :: changed,
                    unchanged)
            ([], [])
        |> fun (changed, unchanged) -> List.rev changed, List.rev unchanged

    let private reviewValues =
        function
        | OperationReview.Case review ->
            let changed, unchanged = caseReviewPresentation review

            {
                Changes = changed
                Unchanged = unchanged
                Effects = []
                Basis = review.Context.Basis
            }
        | OperationReview.Work review ->
            let changed, unchanged =
                changes absent (workEntries review.Before) (workEntries review.Proposed)

            {
                Changes = changed
                Unchanged = unchanged
                Effects = []
                Basis = review.Context.Basis
            }
        | OperationReview.Reminder review ->
            let after =
                match review.Effects with
                | ReminderEffects.Created item
                | ReminderEffects.Changed item -> reminderEntries item
                | ReminderEffects.CompletedAndPlanned(completed, next) ->
                    reminderEntries completed @ reminderEntries next

            let changed, unchanged =
                changes
                    absent
                    (review.Before |> Option.map reminderEntries |> Option.defaultValue [])
                    after

            {
                Changes = changed
                Unchanged = unchanged
                Effects = []
                Basis = review.Context.Basis
            }
        | OperationReview.Authority review ->
            let changed, unchanged =
                changes
                    absent
                    (authorityStateEntries review.Before)
                    (authorityStateEntries review.After)

            {
                Changes = changed
                Unchanged = unchanged
                Effects = authorityEffects review.Effects
                Basis = review.Basis
            }

    let private authoredDetails input =
        match input with
        | Some input ->
            let kind =
                match input with
                | OperationInput.Case item -> ActKind.Case(CaseAct.kind item.Act)
                | OperationInput.Authority item -> ActKind.Authority(AuthorityAct.kind item.Act)
                | OperationInput.Work _ -> ActKind.Work WorkActKind.UpdateWork
                | OperationInput.Reminder item ->
                    ActKind.Reminder(AuthoredReminderAct.kind item.Act)

            let consequence =
                match Consequence.ofAct kind with
                | Consequence.Routine -> []
                | Consequence.Stewardship
                | Consequence.Irreversible ->
                    [ Vocabulary.messageKey ActKind.vocabulary kind "consequence" ]
                    |> List.map (fun key ->
                        section
                            "label.effect"
                            [
                                value
                                    "consequence"
                                    "label.effect"
                                    Prominence.Primary
                                    (PresentedValue.Message key)
                            ])

            let intent =
                match input with
                | OperationInput.Case {
                                          Act = CaseAct.Void reason | CaseAct.Reinstate reason
                                      } ->
                    [
                        section
                            "label.details"
                            [
                                value
                                    "reason"
                                    "field.reason.label"
                                    Prominence.Primary
                                    (PresentedValue.Text reason)
                            ]
                    ]
                | OperationInput.Case {
                                          Act = CaseAct.Correct { Payment = PaymentChange.Reaffirm }
                                      } ->
                    [
                        section
                            (Vocabulary.messageKey
                                RecordGroup.vocabulary
                                RecordGroup.Payment
                                "label")
                            [
                                value
                                    "intent"
                                    "label.action"
                                    Prominence.Primary
                                    (code
                                        CorrectionGroupAction.vocabulary
                                        CorrectionGroupAction.Reaffirm)
                            ]
                    ]
                | _ -> []

            consequence @ intent
        | None -> []

    let operationReview input review =
        let described = reviewValues review

        { described with
            Effects = authoredDetails input @ described.Effects
        }

    /// Clients render the supplied presentation; only disclosed effect/context metadata is projected here.
    let presentedReviewDetails input (review: PresentedReview) =
        let effects, basis =
            match review.Subject with
            | PresentedReviewSubject.Case value -> [], value.Context.Basis
            | PresentedReviewSubject.Work value -> [], value.Context.Basis
            | PresentedReviewSubject.Reminder value -> [], value.Context.Basis
            | PresentedReviewSubject.Authority value -> authorityEffects value.Effects, value.Basis

        authoredDetails input @ effects, basis

    let private receiptHeader reference act =
        section
            "label.recordedChange"
            ([ value "act" "label.action" Prominence.Primary act ]
             @ (reference
                |> Option.toList
                |> List.map (fun reference ->
                    value
                        "reference"
                        "field.caseReference.label"
                        Prominence.Primary
                        (PresentedValue.Reference reference))))

    let private receiptDisclosure key =
        [
            section
                "label.recordedFacts"
                [
                    value
                        "disclosure"
                        "label.details"
                        Prominence.Primary
                        (PresentedValue.Absent key)
                ]
        ]

    let private receiptReminderVersion (version: ReminderVersion) =
        section
            "label.reminder"
            ([
                value
                    "id"
                    "field.reminderId.label"
                    Prominence.Primary
                    (PresentedValue.Identifier(ReminderId.format version.ReminderId))
                value
                    "revision"
                    "label.reminderRevision"
                    Prominence.Detail
                    (PresentedValue.Identifier(Revision.format version.Revision))
             ]
             @ (match version.Snapshot with
                | ReminderDisclosure.Disclosed item ->
                    reminderEntries item
                    |> List.filter (fun entry ->
                        not (entry.Key.EndsWith(".id", StringComparison.Ordinal)))
                | ReminderDisclosure.Withheld ->
                    [
                        value
                            "disclosure"
                            "label.details"
                            Prominence.Primary
                            (PresentedValue.Absent "label.detailsWithheld")
                    ]
                | ReminderDisclosure.Erased ->
                    [
                        value
                            "disclosure"
                            "label.details"
                            Prominence.Primary
                            (PresentedValue.Absent "lifecycle.ERASED.label")
                    ]))

    let private caseReceipt (item: CaseReceipt) =
        [
            receiptHeader (Some item.Reference) (code CaseActKind.vocabulary item.Act)
            section
                "label.recordedFacts"
                [
                    value
                        "revision"
                        "label.revisionLabel"
                        Prominence.Detail
                        (PresentedValue.Identifier(Revision.format item.Revision))
                ]
        ]
        @ (match item.Snapshot with
           | SnapshotDisclosure.Disclosed live ->
               caseFacts
                   {
                       CaseId = item.CaseId
                       Reference = item.Reference
                       Revision = item.Revision
                       State = LiveCase.toState live
                   }
           | SnapshotDisclosure.Withheld -> receiptDisclosure "label.detailsWithheld"
           | SnapshotDisclosure.Erased -> receiptDisclosure "lifecycle.ERASED.label"),
        []

    let private workReceipt (item: WorkReceipt) =
        [
            receiptHeader (Some item.Reference) (code WorkActKind.vocabulary item.Act)
            section
                "section.work.label"
                [
                    value
                        "revision"
                        "label.workRevision"
                        Prominence.Detail
                        (PresentedValue.Identifier(Revision.format item.Revision))
                ]
        ]
        @ (match item.Snapshot with
           | WorkDisclosure.Disclosed work -> [ section "section.work.label" (workEntries work) ]
           | WorkDisclosure.Withheld -> receiptDisclosure "label.detailsWithheld"
           | WorkDisclosure.Erased -> receiptDisclosure "lifecycle.ERASED.label"),
        []

    let private reminderReceipt (item: ReminderReceipt) =
        [
            receiptHeader (Some item.Reference) (code ReminderActKind.vocabulary item.Act)
        ]
        @ (match item.Effects with
           | ReminderReceiptEffects.Single version -> [ receiptReminderVersion version ]
           | ReminderReceiptEffects.CompletedAndPlanned(completed, next) ->
               [ receiptReminderVersion completed; receiptReminderVersion next ]),
        []

    let private authorityReceipt (item: AuthorityReceipt) =
        [
            receiptHeader None (code AuthorityKind.vocabulary item.Act)
            section
                "label.account"
                [
                    value
                        "target"
                        "label.targetAccount"
                        Prominence.Primary
                        (match item.TargetActor with
                         | AuthorityTargetDisclosure.Disclosed actor -> PresentedValue.Actor actor
                         | AuthorityTargetDisclosure.Withheld _ ->
                             PresentedValue.Absent "label.detailsWithheld")
                ]
        ]
        @ (item.Effects
           |> List.map (fun effect ->
               section
                   "label.effect"
                   [
                       value
                           "act"
                           "label.action"
                           Prominence.Primary
                           (code AuthorityKind.vocabulary effect.Act)
                       value
                           "sequence"
                           "label.sequence"
                           Prominence.Detail
                           (PresentedValue.Identifier(Sequence.format effect.Position.Sequence))
                   ])),
        [
            value
                "targetId"
                "input.actorId.label"
                Prominence.Detail
                (PresentedValue.Identifier(
                    ActorId.format (AuthorityTargetDisclosure.actorId item.TargetActor)
                ))
        ]

    let receipt (receipt: OperationReceipt) =
        let body, knownTarget =
            match receipt.Subject with
            | ReceiptSubject.Case item -> caseReceipt item
            | ReceiptSubject.Work item -> workReceipt item
            | ReceiptSubject.Reminder item -> reminderReceipt item
            | ReceiptSubject.Authority item -> authorityReceipt item

        body
        @ [
            section
                "label.recordDetails"
                ([
                    value
                        "operationId"
                        "field.operationId.label"
                        Prominence.Detail
                        (PresentedValue.Identifier(OperationId.format receipt.OperationId))
                    value
                        "sequence"
                        "label.sequence"
                        Prominence.Detail
                        (PresentedValue.Identifier(Sequence.format receipt.Position.Sequence))
                    value
                        "recordedAt"
                        "label.recordedAt"
                        Prominence.Primary
                        (PresentedValue.Instant receipt.RecordedAt)
                    value
                        "recordedBy"
                        "label.recordedBy"
                        Prominence.Primary
                        (PresentedValue.Actor receipt.RecordedBy)
                 ]
                 @ knownTarget)
        ]

    /// Host session presentation contains no antiforgery-token value.
    let session (session: Session) =
        [
            section
                "label.account"
                [
                    value
                        "authenticated"
                        "label.authenticated"
                        Prominence.Primary
                        (boolean session.Authenticated)
                ]
            section
                "label.recordDetails"
                [
                    value
                        "contractIdentity"
                        "label.contractIdentity"
                        Prominence.Detail
                        (PresentedValue.Identifier session.ContractIdentity)
                ]
        ]

    let logout () =
        [
            section
                "label.account"
                [
                    value
                        "result"
                        "label.accountState"
                        Prominence.Primary
                        (PresentedValue.Message "label.signedOut")
                ]
        ]
