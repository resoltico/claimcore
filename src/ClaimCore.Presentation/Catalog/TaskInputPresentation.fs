namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

module TaskInputPresentation =
    let choiceConstraintMessage =
        function
        | TaskInputId.Scope -> Some "label.roleNotAvailableAtScope"
        | TaskInputId.Target InputTarget.PrincipalKind -> Some "label.roleNotAvailableForService"
        | _ -> None

    // This total semantic-input to message-namespace table contains no nested decisions.
    let messageKey input part =
        match input with
        | TaskInputId.CaseDate _ when part = "rule" -> "scalar.calendarDate.guidance"
        | TaskInputId.Target(InputTarget.Field field) ->
            if part = "rule" then
                FieldPresentation.ruleId field
            else
                Vocabulary.messageKey BusinessField.vocabulary field part
        | TaskInputId.Target(InputTarget.Reason | InputTarget.Text | InputTarget.ProfileDisplayName | InputTarget.ProfileContact | InputTarget.PartyName | InputTarget.PaymentReference) when
            part = "rule"
            ->
            "scalar.text.guidance"
        | TaskInputId.Target(InputTarget.Group group) ->
            Vocabulary.messageKey RecordGroup.vocabulary group part
        | TaskInputId.Target target -> Vocabulary.messageKey InputTarget.vocabulary target part
        | TaskInputId.ReminderState
        | TaskInputId.ReminderDue
        | TaskInputId.ReminderKindFilter
        | TaskInputId.DeadlineOutcomeFilter
        | TaskInputId.IncludeInactive
        | TaskInputId.SettleOnly
        | TaskInputId.ConfirmRetirement
        | TaskInputId.Scope
        | TaskInputId.ActorId
        | TaskInputId.CaseId
        | TaskInputId.Lifecycle
        | TaskInputId.Status
        | TaskInputId.Progress
        | TaskInputId.CaseView
        | TaskInputId.ResponsibleActorFilter
        | TaskInputId.RoleFilter
        | TaskInputId.DisclosureCaseId
        | TaskInputId.DisclosureKind
        | TaskInputId.DisclosureFrom
        | TaskInputId.DisclosureUntil
        | TaskInputId.ReminderView
        | TaskInputId.TimelineFilter
        | TaskInputId.Order
        | TaskInputId.Due
        | TaskInputId.CaseDate _
        | TaskInputId.GroupBy
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
        | TaskInputId.ReminderKindFilter
        | TaskInputId.DeadlineOutcomeFilter
        | TaskInputId.CaseDate _
        | TaskInputId.GroupBy
        | TaskInputId.SettleOnly
        | TaskInputId.ConfirmRetirement
        | TaskInputId.IncludeInactive -> None
        | _ -> Some(messageKey input "hint")

    let scalar =
        function
        | TaskInputId.Target(InputTarget.Field field) -> Some(FieldPresentation.descriptor field)
        | TaskInputId.Target(InputTarget.NewCaseReference | InputTarget.LinkTargetReference) ->
            Some(FieldPresentation.descriptor BusinessField.CaseReference)
        | TaskInputId.Target InputTarget.LinkNote -> Some(ScalarRule.PlainText NoteText.constraints)
        | TaskInputId.CaseDate _ -> Some(ScalarRule.PlannedDate CalendarDate.descriptor)
        | TaskInputId.Target(InputTarget.ProfileDisplayName | InputTarget.Reason) ->
            Some(ScalarRule.Text PartyName.descriptor)
        | TaskInputId.Target InputTarget.ProfileDigestEmail ->
            Some(ScalarRule.Text EmailAddress.descriptor)
        | TaskInputId.Target InputTarget.ProfileContact ->
            Some(ScalarRule.Text ContactText.descriptor)
        | TaskInputId.Target InputTarget.PartyName -> Some(ScalarRule.Text PartyName.descriptor)
        | TaskInputId.Target InputTarget.PartyDetails ->
            Some(ScalarRule.PlainText PartyDetails.constraints)
        | TaskInputId.Target InputTarget.PaymentReference ->
            Some(ScalarRule.Text Identifier.descriptor)
        | TaskInputId.Target InputTarget.NoteText -> Some(ScalarRule.PlainText NoteText.constraints)
        | TaskInputId.Target InputTarget.ReminderAction ->
            Some(ScalarRule.PlainText ReminderAction.constraints)
        | TaskInputId.Target InputTarget.ReminderDueDate ->
            Some(ScalarRule.PlannedDate CalendarDate.descriptor)
        | TaskInputId.Target InputTarget.ReminderDoneOn ->
            Some(ScalarRule.CalendarDate CalendarDate.descriptor)
        | TaskInputId.Target InputTarget.DirectorySearch ->
            Some(
                ScalarRule.Text
                    { PartyName.descriptor with
                        MaximumCharacters = Some DirectorySearchText.MaximumCharacters
                    }
            )
        | TaskInputId.Target InputTarget.Text ->
            Some(
                ScalarRule.Text
                    { PartyName.descriptor with
                        MaximumCharacters = Some CaseSearchText.MaximumCharacters
                    }
            )
        | _ -> None
