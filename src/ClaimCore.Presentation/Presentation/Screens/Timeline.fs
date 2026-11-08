namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module TimelineScreen =
    let difference key label format (change: ValueChange<'value>) =
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

    let reminderDifference (change: ReminderDifference) =
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
            column "changes" "label.changes" Prominence.Secondary false (fun item ->
                match item.Changes with
                | TimelineChanges.Disclosed disclosure ->
                    PresentedValue.Count(int64 disclosure.Items.Length)
                | TimelineChanges.Withheld -> PresentedValue.Message "label.changesWithheld"
                | TimelineChanges.Erased -> PresentedValue.Message "label.changesErased")
        ]

    let timelineDetail (entry: CaseTimelineEntry) =
        let summary, highlights =
            match entry.Changes with
            | TimelineChanges.Disclosed disclosure ->
                let event = TimelineEventPresentation.describe disclosure.Overview.Event

                let highlights =
                    TimelineDisclosure.resolve disclosure
                    |> Result.defaultWith invalidOp
                    |> List.collect timelineChange

                Some(
                    {
                        MessageId = event.MessageId
                        Arguments = event.Arguments
                    }
                    : PresentedMessage
                ),
                highlights
            | TimelineChanges.Withheld
            | TimelineChanges.Erased -> None, []

        Some
            {
                Summary = summary
                Highlights = highlights
                Sections = timelineEntry entry
            }


    let item (entry: CaseTimelineEntry) =
        let detail = timelineDetail entry |> Option.get

        let act =
            match entry.Subject with
            | TimelineSubject.CaseFact(_, act) -> ActKind.Case act
            | TimelineSubject.Work(_, act) -> ActKind.Work act
            | TimelineSubject.Reminder(act, _, _, _) -> ActKind.Reminder act

        let summary =
            defaultArg
                detail.Summary
                {
                    MessageId = Vocabulary.messageKey ActKind.vocabulary act "label"
                    Arguments = Map.empty
                }

        let changes =
            detail.Sections
            |> List.mapi (fun index section ->
                if index = 0 then
                    { section with
                        Entries = section.Entries |> List.filter technical
                    }
                else
                    section)
            |> List.filter (fun section -> not section.Entries.IsEmpty)

        {
            RegionTimelineEntry.Key = Sequence.format entry.Position.Sequence
            When = entry.RecordedAt
            Who = entry.RecordedBy
            Summary = summary
            Highlights = detail.Highlights
            Details =
                changes
                |> List.filter (fun section -> not (List.contains section detail.Highlights))
        }

    let rows rows =
        Screen.create
            [ region RegionRole.Activity None (RegionContent.Timeline(List.map item rows)) ]

    let view (reply: CaseTimelinePage) =
        rows reply.Items |> page reply.Basis reply.Items.Length reply.NextCursor
