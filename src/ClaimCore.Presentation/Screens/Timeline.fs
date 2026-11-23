namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module TimelineScreen =
    let changedFieldLabels comparisons facts =
        [
            for (change: PresentedChange) in comparisons do
                if change.Before <> change.After then
                    change.LabelId
            yield! facts
        ]
        |> List.distinct

    let difference key label format (change: ValueChange<'value>) =
        {
            TitleId = label
            EmptyMessageId = None
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
            match change.Kind with
            | Some change ->
                yield
                    difference
                        (prefix + "kind")
                        "field.kind.label"
                        (code ReminderKind.vocabulary)
                        change
            | None -> ()
            match change.DoneOn with
            | Some change ->
                yield difference (prefix + "doneOn") "field.doneOn.label" PresentedValue.Date change
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

    let private paymentSections compact (changes: PaymentCollectionDifference) =
        [
            for change in changes.Entries do
                let entry = PaymentDifference.entry change

                let prefix = "payment." + OperationId.format (PaymentEntry.id entry)

                yield
                    section
                        "label.payments"
                        [
                            value
                                (prefix + ".date")
                                "field.paymentDate.label"
                                Prominence.Primary
                                (PresentedValue.Date(PaymentEntry.date entry))
                            value
                                (prefix + ".amount")
                                "field.paidAmount.label"
                                Prominence.Primary
                                (PresentedValue.money (PaymentEntry.money entry))
                            value
                                (prefix + ".direction")
                                "field.direction.label"
                                Prominence.Secondary
                                (code PaymentDirection.vocabulary (PaymentEntry.direction entry))
                            value
                                (prefix + ".reference")
                                "field.reference.label"
                                Prominence.Detail
                                (PaymentEntry.reference entry
                                 |> optional (Identifier.format >> PresentedValue.Identifier))
                            if not compact then
                                value
                                    (prefix + ".id")
                                    "field.paymentId.label"
                                    Prominence.Detail
                                    (PresentedValue.Identifier(
                                        OperationId.format (PaymentEntry.id entry)
                                    ))
                        ]
            match changes.After with
            | Some totals when compact ->
                yield
                    section
                        "label.paymentTotals"
                        [
                            value
                                "netPaid"
                                "label.netPaid"
                                Prominence.Primary
                                (PaymentsScreen.netPaid totals)
                            value
                                "payable"
                                "field.payableAmount.label"
                                Prominence.Primary
                                (PresentedValue.money totals.Payable)
                        ]
            | _ -> ()
            if not compact then
                yield
                    difference
                        "netPaid"
                        "label.netPaid"
                        PaymentsScreen.netPaid
                        {
                            Before = changes.Before
                            After = changes.After
                        }
        ]

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
        | TimelineChange.Disposition disposition ->
            [
                section
                    "field.reason.label"
                    [
                        value
                            "reason"
                            "field.reason.label"
                            Prominence.Primary
                            (PresentedValue.Narrative(
                                Reason.format (CaseDisposition.reason disposition)
                            ))
                    ]
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
        | TimelineChange.Party(_, change) ->
            [
                difference
                    "role"
                    "field.partyRole.label"
                    (code PartyRole.vocabulary)
                    {
                        Before = change.Before |> Option.map _.Role
                        After = change.After |> Option.map _.Role
                    }
                difference
                    "name"
                    "field.partyName.label"
                    PresentedValue.Party
                    {
                        Before = change.Before |> Option.bind _.Name
                        After = change.After |> Option.bind _.Name
                    }
                difference
                    "details"
                    "field.partyDetails.label"
                    (PartyDetails.format >> PresentedValue.Narrative)
                    {
                        Before = change.Before |> Option.bind _.Details
                        After = change.After |> Option.bind _.Details
                    }
            ]
        | TimelineChange.Attachment(_, _, display) ->
            display
            |> Option.map (AttachmentsScreen.details >> section "section.attachments.label")
            |> Option.toList
        | TimelineChange.Note(_, _, text) ->
            [
                section
                    "section.notes.label"
                    [
                        value
                            "noteText"
                            "field.noteText.label"
                            Prominence.Secondary
                            (text |> optional (NoteText.format >> PresentedValue.Narrative))
                    ]
            ]
        | TimelineChange.Payments change -> paymentSections false change

    let timelineEntry (entry: CaseTimelineEntry) =
        let act = PresentedValue.Message("task." + TaskId.token entry.Task + ".label")
        let revision = Some entry.Subject.Revision

        let identifiers = [ partIdentity entry.Subject.PartKind entry.Subject.PartId ]

        [
            yield
                {
                    TitleId = "label.changes"
                    EmptyMessageId = None
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
                            match revision with
                            | Some revision ->
                                yield
                                    value
                                        "revision"
                                        "label.revisionLabel"
                                        Prominence.Detail
                                        (PresentedValue.Identifier(Revision.format revision))
                            | None -> ()
                            yield
                                value
                                    "operationId"
                                    "field.operationId.label"
                                    Prominence.Detail
                                    (PresentedValue.Identifier(OperationId.format entry.OperationId))
                            yield
                                value
                                    "effectPosition"
                                    "label.effectPosition"
                                    Prominence.Detail
                                    (PresentedValue.Identifier(EffectPosition.format entry.Position))
                            yield
                                value
                                    "operationHash"
                                    "label.operationHash"
                                    Prominence.Detail
                                    (PresentedValue.Identifier(
                                        entry.OperationHash
                                        |> EntryHash.toBytes
                                        |> Convert.ToHexStringLower
                                    ))
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
                        EmptyMessageId = None
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
                        EmptyMessageId = None
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
                PresentedValue.Message("task." + TaskId.token item.Task + ".label"))
            column "recordedAt" "label.recordedAt" Prominence.Secondary false (fun item ->
                PresentedValue.Instant item.RecordedAt)
            column "sequence" "label.effectPosition" Prominence.Detail false (fun item ->
                PresentedValue.Identifier(EffectPosition.format item.Position))
            column "operationHash" "label.operationHash" Prominence.Detail false (fun item ->
                PresentedValue.Identifier(
                    item.OperationHash |> EntryHash.toBytes |> Convert.ToHexStringLower
                ))
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
                    |> List.collect (function
                        | TimelineChange.Payments change -> paymentSections true change
                        | change -> timelineChange change)

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


    let private compareSection (section: Section) =
        match section.Entries with
        | [ before; after ] when before.LabelId = "label.before" && after.LabelId = "label.after" ->
            Some
                {
                    PresentedChange.Key = before.Key
                    LabelId = section.TitleId
                    Before = before.Value
                    After = after.Value
                }
        | _ -> None

    let item (entry: CaseTimelineEntry) =
        let detail = timelineDetail entry |> Option.get

        let summary =
            defaultArg
                detail.Summary
                {
                    MessageId = "task." + TaskId.token entry.Task + ".label"
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

        let comparisons, other = changes |> List.partition (compareSection >> Option.isSome)
        let comparisons = comparisons |> List.choose compareSection

        let changedFields =
            match entry.Changes with
            | TimelineChanges.Disclosed _ ->
                changedFieldLabels comparisons (other |> List.skip 1 |> List.map _.TitleId)
            | _ -> []

        let highlights =
            match entry.Changes with
            | TimelineChanges.Disclosed _ ->
                [
                    section
                        "label.changes"
                        [
                            value
                                "changes"
                                "label.changes"
                                Prominence.Secondary
                                (PresentedValue.Count(int64 (comparisons.Length + other.Length - 1)))
                        ]
                ]
            | _ -> other |> List.skip 1

        {
            RegionTimelineEntry.Key =
                EffectPosition.format entry.Position
                + "."
                + OperationId.format entry.OperationId
            Target = None
            Changes = comparisons
            ChangedFields = changedFields
            Link = None
            Actions = []
            When = entry.RecordedAt
            Who = PresentedValue.Actor entry.RecordedBy
            Summary = summary
            Highlights = highlights
            Details =
                if
                    entry.Changes = TimelineChanges.Withheld
                    || entry.Changes = TimelineChanges.Erased
                then
                    List.truncate 1 other
                else
                    other
        }

    let rows entries =
        Screen.create
            [
                region RegionRole.Activity None (RegionContent.Timeline(List.map item entries))
                |> withEmpty "empty.activity"
                |> withCount "activity" (int64 entries.Length)
            ]

    let view (reply: CaseTimelinePage) = rows reply.Items
