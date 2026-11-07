namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Presentation

module internal TextWorkReply =
    open TextReplyFormat

    let work language (snapshot: CaseWorkReadModel) =
        [
            line language "label.workRevision" (Revision.format snapshot.Revision)
            line
                language
                (FieldPresentation.labelId BusinessField.ResponsibleActor)
                (snapshot.Responsible |> Option.map (actorName language) |> displayed language)
            line
                language
                (FieldPresentation.labelId BusinessField.HandlingSummary)
                (snapshot.Summary |> Option.map HandlingSummary.format |> narrative language)
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
        let header, rows =
            TextTable.page language (TextTable.environment ()) Descriptor.reminderColumns page.Items

        (if header = "" then [] else [ header ])
        @ rows page.Items
        @ TextObservation.footer language page.Items.Length page.NextCursor page.Basis true

    let workViews language (value: WorkViews) =
        TextDescriptorValue.sections language (Descriptor.workViews value)
        @ basis language value.Basis
