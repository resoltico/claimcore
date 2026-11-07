namespace ClaimCore.Cli

open System.Text
open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// CSV exports the supplied typed differences as canonical, unlocalized scalars.
module internal TimelineCsv =
    let private difference key format (change: ValueChange<'value>) =
        [
            key
            change.Before |> Option.map format |> Option.defaultValue ""
            change.After |> Option.map format |> Option.defaultValue ""
        ]

    let private changes =
        function
        | TimelineChange.CaseField change ->
            [
                difference
                    (BusinessField.key change.FieldName)
                    CaseFieldValue.format
                    {
                        Before = change.Before
                        After = change.After
                    }
            ]
        | TimelineChange.CaseMoney change ->
            [
                difference
                    (BusinessField.key change.FieldName)
                    (fun money -> Money.format money + " " + Money.currency money)
                    change.Change
            ]
        | TimelineChange.Lifecycle change ->
            [
                difference
                    "lifecycle"
                    Lifecycle.token
                    {
                        Before = change.Before
                        After = Some change.After
                    }
            ]
        | TimelineChange.Responsibility change ->
            [
                difference
                    "responsibleActor"
                    (fun (actor: ActorRef) -> ActorId.format actor.ActorId)
                    change
            ]
        | TimelineChange.HandlingSummary change ->
            [ difference "handlingSummary" HandlingSummary.format change ]
        | TimelineChange.Reminder change ->
            let prefix = "reminder." + ReminderId.format change.ReminderId + "."

            [
                yield!
                    change.Action
                    |> Option.toList
                    |> List.map (difference (prefix + "action") ReminderAction.format)
                yield!
                    change.Due
                    |> Option.toList
                    |> List.map (difference (prefix + "dueDate") CalendarDate.format)
                yield!
                    change.State
                    |> Option.toList
                    |> List.map (difference (prefix + "state") ReminderState.token)
            ]

    let private csv cells =
        (cells |> List.map CsvCell.format |> String.concat ",") + "\r\n"
        |> Encoding.UTF8.GetBytes

    let csvHeader =
        csv
            [
                "caseReference"
                "operationId"
                "sequence"
                "recordedAt"
                "recordedBy.actorId"
                "recordedBy.displayName.value"
                "recordedBy.displayName.kind"
                "act"
                "revision"
                "reminderId"
                "nextReminderId"
                "nextReminderRevision"
                "changes.kind"
                "field"
                "before"
                "after"
            ]

    let private rows reference (entry: CaseTimelineEntry) =
        let act, revision, reminder, nextId, nextRevision =
            match entry.Subject with
            | TimelineSubject.CaseFact(revision, act) ->
                CaseActKind.token act, Revision.format revision, "", "", ""
            | TimelineSubject.Work(revision, act) ->
                WorkActKind.token act, Revision.format revision, "", "", ""
            | TimelineSubject.Reminder(act, id, revision, next) ->
                ReminderActKind.token act,
                Revision.format revision,
                ReminderId.format id,
                (next |> Option.map (fst >> ReminderId.format) |> Option.defaultValue ""),
                (next |> Option.map (snd >> Revision.format) |> Option.defaultValue "")

        let actorName, actorKind =
            match entry.RecordedBy.DisplayName with
            | DisplayName.Named value -> PartyName.format value, "NAMED"
            | DisplayName.Erased -> "", "ERASED"

        let disclosure =
            match entry.Changes with
            | TimelineChanges.Disclosed _ -> "DISCLOSED"
            | TimelineChanges.Withheld -> "WITHHELD"
            | TimelineChanges.Erased -> "ERASED"

        let metadata =
            [
                CaseReference.format reference
                OperationId.format entry.OperationId
                Sequence.format entry.Position.Sequence
                ReplyTextFormat.instant entry.RecordedAt
                ActorId.format entry.RecordedBy.ActorId
                actorName
                actorKind
                act
                revision
                reminder
                nextId
                nextRevision
                disclosure
            ]

        let differences =
            match entry.Changes with
            | TimelineChanges.Disclosed disclosure -> disclosure.Items |> List.collect changes
            | _ -> []

        let differences =
            if differences.IsEmpty then
                [ [ ""; ""; "" ] ]
            else
                differences

        differences |> List.map (fun values -> csv (metadata @ values))

    let csvRows (page: CaseTimelinePage) =
        page.Items |> List.collect (rows page.Reference) |> Array.concat
