namespace ClaimCore.Protocol.Presentation

open ClaimCore.Domain

type TimelineEventDescription =
    {
        MessageId: string
        Arguments: Map<string, PresentedValue>
    }

/// Recorded-event wording and argument meaning belong to the Core; interfaces only format typed values.
module TimelineEventPresentation =
    let caseKind =
        function
        | CaseActKind.Open -> "CASE_OPENED", "timeline.event.caseOpened"
        | CaseActKind.AmendRegistration ->
            "REGISTRATION_AMENDED", "timeline.event.registrationAmended"
        | CaseActKind.Correct -> "FACTS_CORRECTED", "timeline.event.factsCorrected"
        | CaseActKind.Decide -> "DECISION_RECORDED", "timeline.event.decisionRecorded"
        | CaseActKind.WithdrawDecision -> "DECISION_WITHDRAWN", "timeline.event.decisionWithdrawn"
        | CaseActKind.RecordPayment -> "PAYMENT_RECORDED", "timeline.event.paymentRecorded"
        | CaseActKind.ClearPayment -> "PAYMENT_CLEARED", "timeline.event.paymentCleared"
        | CaseActKind.Close -> "CASE_CLOSED", "timeline.event.caseClosed"
        | CaseActKind.Reopen -> "CASE_REOPENED", "timeline.event.caseReopened"
        | CaseActKind.Void -> "CASE_VOIDED", "timeline.event.caseVoided"
        | CaseActKind.Reinstate -> "CASE_REINSTATED", "timeline.event.caseReinstated"
        | CaseActKind.Erase -> "CASE_ERASED", "timeline.event.caseErased"

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
        | TimelineEvent.CaseActRecorded act ->
            Some
                {
                    MessageId = caseKind act |> snd
                    Arguments = Map.empty
                }
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
            for act in CaseActKind.all do
                yield caseKind act |> snd, Set.empty
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
