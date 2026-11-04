namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Protocol.Presentation

module internal TextWorkReply =
    open TextReplyFormat

    let work language (snapshot: CaseWorkSnapshot) =
        [
            line language "label.workRevision" (Revision.format snapshot.Revision)
            line
                language
                (FieldPresentation.labelId BusinessField.ResponsibleActor)
                (snapshot.Work.Responsible |> Option.map ActorId.format |> displayed language)
            line
                language
                (FieldPresentation.labelId BusinessField.HandlingSummary)
                (snapshot.Work.Summary |> Option.map HandlingSummary.format |> narrative language)
        ]

    let reminder language value =
        [
            line language "field.reminderId.label" (ReminderId.format (Reminder.id value))
            line language "label.reminderRevision" (Revision.format (Reminder.revision value))
            line
                language
                "input.reminderState.label"
                (term language ReminderState.vocabulary (Reminder.state value))
            line
                language
                "field.reminderAction.label"
                (Reminder.action value |> ReminderAction.format |> Some |> displayed language)
            line language "field.reminderDueDate.label" (Reminder.due value |> CalendarDate.format)
        ]

    let effects language =
        function
        | ReminderEffects.Created value
        | ReminderEffects.Changed value -> reminder language value
        | ReminderEffects.CompletedAndPlanned(completed, next) ->
            reminder language completed @ reminder language next

    let private basis language value = [ TextObservation.line language value ]

    let private currentReminder language (value: ReminderReadModel) =
        TextDescriptorValue.sections language (Descriptor.reminder value)
        @ [
            value.Acts
            |> List.map (fun act -> term language ReminderActKind.vocabulary act)
            |> String.concat ", "
        ]

    let single language (value: ReminderRead) =
        currentReminder language value.Item @ basis language value.Basis

    let page language (page: ReminderPage) =
        (page.Items |> List.collect (currentReminder language))
        @ nextLines language page.NextCursor
        @ basis language page.Basis

    let caseViews language (value: CaseViews) =
        TextDescriptorValue.entries language (Descriptor.caseViews value)
        @ basis language value.Basis

    let reminderViews language (value: ReminderViews) =
        TextDescriptorValue.entries language (Descriptor.reminderViews value)
        @ basis language value.Basis

    let overview language (value: WorkOverview) =
        [ line language "input.mine.label" (boolean value.Mine) ]
        @ TextDescriptorValue.entries language (Descriptor.workOverview value)
        @ basis language value.Basis

    let activity language (page: CaseActivityPage) =
        (page.Items
         |> List.collect (fun item ->
             let act, revision =
                 match item.Subject with
                 | ActivitySubject.Case(revision, act) ->
                     term language CaseActKind.vocabulary act, Revision.format revision
                 | ActivitySubject.Work(revision, act) ->
                     term language WorkActKind.vocabulary act, Revision.format revision
                 | ActivitySubject.Reminder(act, id, revision, _) ->
                     term language ReminderActKind.vocabulary act + " " + ReminderId.format id,
                     Revision.format revision

             [
                 Sequence.format item.Position.Sequence
                 + "  "
                 + act
                 + "  "
                 + revision
                 + "  "
                 + instant item.RecordedAt
                 + "  "
                 + actorName language item.RecordedBy
             ]
             @ (match item.Detail |> Option.map _.Subject with
                | Some(ReceiptSubject.Work {
                                               Snapshot = WorkDisclosure.Disclosed snapshot
                                           }) -> work language snapshot
                | Some(ReceiptSubject.Reminder value) ->
                    let one (version: ReminderVersion) =
                        match version.Snapshot with
                        | ReminderDisclosure.Disclosed value -> reminder language value
                        | _ -> []

                    match value.Effects with
                    | ReminderReceiptEffects.Single value -> one value
                    | ReminderReceiptEffects.CompletedAndPlanned(completed, next) ->
                        one completed @ one next
                | _ -> [])))
        @ nextLines language page.NextCursor
