namespace ClaimCore.Protocol.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol

[<RequireQualifiedAccess>]
type PresentedValue =
    | Money of Money
    | Date of CalendarDate
    | Instant of DateTimeOffset
    | Party of PartyName
    | Text of string
    | Code of vocabulary: string * token: string
    | Actor of ActorRef
    | State of CaseHeadline
    | Count of int64
    | Reference of CaseReference
    | Identifier of string
    | Absent of reasonId: string

type Entry =
    {
        Key: string
        LabelId: string
        Prominence: Prominence
        Value: PresentedValue
    }

type Section =
    { TitleId: string; Entries: Entry list }

/// Column metadata is independent of values, so empty pages and absent facts retain stable headers.
[<NoEquality; NoComparison>]
type EntryDefinition<'reply> =
    {
        Key: string
        LabelId: string
        Prominence: Prominence
        IsMoney: bool
        Read: 'reply -> PresentedValue
    }

module Descriptor =
    let entries definitions value =
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
            PresentedValue.Text(IncidentDescription.format value)
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
                                PresentedValue.Money(Money.create amount code)
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
                        (optional (HandlingSummary.format >> PresentedValue.Text) work.Summary)
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
                    PresentedValue.Money
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
                    (HandlingSummary.format >> PresentedValue.Text)
                    change
            ]
        | TimelineChange.Reminder change -> reminderDifference change

    let timelineEntry (entry: CaseTimelineEntry) =
        let act, revision, identifiers =
            match entry.Subject with
            | TimelineSubject.CaseFact(revision, act) -> CaseActKind.token act, revision, []
            | TimelineSubject.Work(revision, act) -> WorkActKind.token act, revision, []
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

                ReminderActKind.token act, revision, identifiers

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
                            yield
                                value
                                    "act"
                                    "label.action"
                                    Prominence.Primary
                                    (PresentedValue.Code("act", act))
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
            | TimelineChanges.Disclosed changes -> yield! changes |> List.collect timelineChange
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
