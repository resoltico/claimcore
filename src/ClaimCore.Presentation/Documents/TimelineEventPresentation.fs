namespace ClaimCore.Presentation

open ClaimCore.Domain

type TimelineEventDescription =
    {
        MessageId: string
        Arguments: Map<string, PresentedValue>
    }

/// Recorded-event wording and argument meaning belong to the Core; interfaces only format typed values.
module TimelineEventPresentation =
    let private description key arguments =
        {
            MessageId = "timeline.event." + key
            Arguments = Map.ofList arguments
        }

    let private withArguments =
        function
        | TimelineEvent.ResponsibilityAssigned person ->
            description "responsibilityAssigned" [ "person", PresentedValue.Actor person ]
        | TimelineEvent.ResponsibilityCleared person ->
            description "responsibilityCleared" [ "person", PresentedValue.Actor person ]
        | TimelineEvent.ReminderPlanned(action, due) ->
            description
                "reminderPlanned"
                [
                    "action", PresentedValue.Text(ReminderAction.format action)
                    "due", PresentedValue.Date due
                ]
        | TimelineEvent.ReminderChanged action ->
            description
                "reminderChanged"
                [ "action", PresentedValue.Text(ReminderAction.format action) ]
        | TimelineEvent.ReminderCompleted action ->
            description
                "reminderCompleted"
                [ "action", PresentedValue.Text(ReminderAction.format action) ]
        | TimelineEvent.ReminderCancelled action ->
            description
                "reminderCancelled"
                [ "action", PresentedValue.Text(ReminderAction.format action) ]
        | TimelineEvent.ReminderReopened(action, due) ->
            description
                "reminderReopened"
                [
                    "action", PresentedValue.Text(ReminderAction.format action)
                    "due", PresentedValue.Date due
                ]
        | TimelineEvent.ReminderCompletedAndPlanned(completed, next, due) ->
            description
                "reminderCompletedAndPlanned"
                [
                    "completedAction", PresentedValue.Text(ReminderAction.format completed)
                    "nextAction", PresentedValue.Text(ReminderAction.format next)
                    "nextDue", PresentedValue.Date due
                ]
        | _ -> invalidArg "event" "Use an event with typed arguments."

    let private withoutArguments =
        function
        | TimelineEvent.PartChanged kind ->
            Some
                {
                    MessageId =
                        "change." + ClaimCore.Domain.Changes.PartKind.token kind + ".recorded"
                    Arguments = Map.empty
                }
        | TimelineEvent.NoteRecorded removed ->
            Some(description (if removed then "noteRemoved" else "noteAdded") [])
        | TimelineEvent.WorkRecorded -> Some(description "workRecorded" [])
        | TimelineEvent.HandlingSummaryAdded -> Some(description "handlingSummaryAdded" [])
        | TimelineEvent.HandlingSummaryChanged -> Some(description "handlingSummaryChanged" [])
        | TimelineEvent.HandlingSummaryCleared -> Some(description "handlingSummaryCleared" [])
        | _ -> None

    let describe event =
        withoutArguments event |> Option.defaultWith (fun () -> withArguments event)

    /// Message argument contracts are checked before generated catalogs reach either renderer.
    let contracts =
        [
            for kind in Vocabulary.all ClaimCore.Domain.Changes.PartKind.vocabulary do
                yield
                    "change." + ClaimCore.Domain.Changes.PartKind.token kind + ".recorded",
                    Set.empty
            yield "timeline.event.noteRemoved", Set.empty
            yield "timeline.event.noteAdded", Set.empty
            for key, arguments in
                [
                    "responsibilityAssigned", [ "person" ]
                    "responsibilityCleared", [ "person" ]
                    "workRecorded", []
                    "handlingSummaryAdded", []
                    "handlingSummaryChanged", []
                    "handlingSummaryCleared", []
                    "reminderPlanned", [ "action"; "due" ]
                    "reminderChanged", [ "action" ]
                    "reminderCompleted", [ "action" ]
                    "reminderCancelled", [ "action" ]
                    "reminderReopened", [ "action"; "due" ]
                    "reminderCompletedAndPlanned", [ "completedAction"; "nextAction"; "nextDue" ]
                ] do
                yield "timeline.event." + key, Set.ofList arguments
        ]
        |> Map.ofList
