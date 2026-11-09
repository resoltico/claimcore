namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

module TaskInputPresentation =
    // This total semantic-input to message-namespace table contains no nested decisions.
    let messageKey input part =
        match input with
        | TaskInputId.Target(InputTarget.Field field) ->
            if part = "rule" then
                FieldPresentation.ruleId field
            else
                Vocabulary.messageKey BusinessField.vocabulary field part
        | TaskInputId.Target(InputTarget.Reason | InputTarget.Text | InputTarget.ProfileDisplayName | InputTarget.ProfileContact) when
            part = "rule"
            ->
            "scalar.text.guidance"
        | TaskInputId.Target(InputTarget.Group group) ->
            Vocabulary.messageKey RecordGroup.vocabulary group part
        | TaskInputId.Target target -> Vocabulary.messageKey InputTarget.vocabulary target part
        | TaskInputId.ReminderState
        | TaskInputId.ReminderDue
        | TaskInputId.IncludeInactive
        | TaskInputId.ResponsibilityChange
        | TaskInputId.Scope
        | TaskInputId.ActorId
        | TaskInputId.CaseId
        | TaskInputId.Lifecycle
        | TaskInputId.Status
        | TaskInputId.Progress
        | TaskInputId.CaseView
        | TaskInputId.ResponsibleActorFilter
        | TaskInputId.RoleFilter
        | TaskInputId.ReminderView
        | TaskInputId.TimelineFilter
        | TaskInputId.Order
        | TaskInputId.Due
        | TaskInputId.Assignment ->
            TaskInputId.vocabularyName + "." + TaskInputId.key input + "." + part

    let hintId input =
        match input with
        | TaskInputId.Lifecycle
        | TaskInputId.Progress
        | TaskInputId.CaseView
        | TaskInputId.ResponsibleActorFilter
        | TaskInputId.ReminderView
        | TaskInputId.Assignment
        | TaskInputId.Due
        | TaskInputId.ReminderState
        | TaskInputId.ReminderDue
        | TaskInputId.IncludeInactive -> None
        | _ -> Some(messageKey input "hint")
