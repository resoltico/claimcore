namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol

/// Render only the typed differences supplied by the core; no history is reconstructed here.
module internal TextTimelineReply =
    open TextReplyFormat

    let private field language =
        function
        | CaseFieldValue.Reference value -> CaseReference.format value
        | CaseFieldValue.Status value -> term language CaseStatus.vocabulary value
        | CaseFieldValue.Business value -> FieldValue.format value

    let private delta language key format (change: ValueChange<'value>) =
        line
            language
            key
            (displayed language (change.Before |> Option.map format)
             + " → "
             + displayed language (change.After |> Option.map format))

    let private reminder language (change: ReminderDifference) =
        [ line language "field.reminderId.label" (ReminderId.format change.ReminderId) ]
        @ (change.Action
           |> Option.map (delta language "field.reminderAction.label" ReminderAction.format)
           |> Option.toList)
        @ (change.Due
           |> Option.map (delta language "field.reminderDueDate.label" CalendarDate.format)
           |> Option.toList)
        @ (change.State
           |> Option.map (
               delta language "input.reminderState.label" (term language ReminderState.vocabulary)
           )
           |> Option.toList)

    let private change language =
        function
        | TimelineChange.CaseField value ->
            delta
                language
                (FieldPresentation.labelId value.FieldName)
                (field language)
                {
                    Before = value.Before
                    After = value.After
                }
            |> List.singleton
        | TimelineChange.CaseMoney value ->
            delta language (FieldPresentation.labelId value.FieldName) (money language) value.Change
            |> List.singleton
        | TimelineChange.Lifecycle value ->
            delta
                language
                "input.lifecycle.label"
                (term language Lifecycle.vocabulary)
                {
                    Before = value.Before
                    After = Some value.After
                }
            |> List.singleton
        | TimelineChange.Responsibility value ->
            delta language "field.responsibleActor.label" (actorName language) value
            |> List.singleton
        | TimelineChange.HandlingSummary value ->
            delta language "field.handlingSummary.label" HandlingSummary.format value
            |> List.singleton
        | TimelineChange.Reminder value -> reminder language value

    let private subject language =
        function
        | TimelineSubject.CaseFact(revision, act) ->
            [ term language CaseActKind.vocabulary act; Revision.format revision ]
        | TimelineSubject.Work(revision, act) ->
            [ term language WorkActKind.vocabulary act; Revision.format revision ]
        | TimelineSubject.Reminder(act, id, revision, next) ->
            [
                term language ReminderActKind.vocabulary act
                ReminderId.format id
                Revision.format revision
            ]
            @ (next
               |> Option.map (fun (id, revision) ->
                   "→ " + ReminderId.format id + " " + Revision.format revision)
               |> Option.toList)

    let page language (page: CaseTimelinePage) =
        [ displayed language (Some(CaseReference.format page.Reference)) ]
        @ (page.Items
           |> List.collect (fun entry ->
               [
                   String.concat
                       "  "
                       ([
                           instant entry.RecordedAt
                           displayed language (Some(actorName language entry.RecordedBy))
                        ]
                        @ subject language entry.Subject)
               ]
               @ (match entry.Changes with
                  | TimelineChanges.Disclosed changes -> changes |> List.collect (change language)
                  | TimelineChanges.Withheld ->
                      [ label language (CliMessages.key CliMessage.TimelineWithheld) ]
                  | TimelineChanges.Erased ->
                      [ label language (CliMessages.key CliMessage.TimelineErased) ])))
        @ nextLines language page.NextCursor
