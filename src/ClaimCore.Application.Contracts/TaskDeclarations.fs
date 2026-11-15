namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type TaskChoiceId =
    | Role
    | PrincipalKind
    | Scope
    | Lifecycle
    | Status
    | Progress
    | CaseView
    | RoleFilter
    | ReminderView
    | TimelineFilter
    | Order
    | Assignment
    | Due
    | ReminderState
    | ReminderDue
    | RegistrationCorrection
    | DecisionCorrection

module TaskChoiceId =
    let vocabulary =
        Vocabulary.ofUnion<TaskChoiceId> "taskChoice" TokenCasing.CamelCase None

    let all = Vocabulary.all vocabulary

    let private vocabularyTokens vocabulary =
        Vocabulary.all vocabulary |> List.map (Vocabulary.token vocabulary)

    let tokens =
        function
        | TaskChoiceId.Role
        | TaskChoiceId.RoleFilter -> vocabularyTokens Role.vocabulary
        | TaskChoiceId.PrincipalKind -> vocabularyTokens PrincipalKind.vocabulary
        | TaskChoiceId.Scope -> vocabularyTokens ScopeKind.vocabulary
        | TaskChoiceId.Lifecycle -> vocabularyTokens Lifecycle.vocabulary
        | TaskChoiceId.Status -> vocabularyTokens CaseStatus.vocabulary
        | TaskChoiceId.Progress -> vocabularyTokens CaseProgress.vocabulary
        | TaskChoiceId.CaseView -> vocabularyTokens CaseView.vocabulary
        | TaskChoiceId.ReminderView -> vocabularyTokens ReminderView.vocabulary
        | TaskChoiceId.TimelineFilter -> vocabularyTokens TimelineFilter.vocabulary
        | TaskChoiceId.Order -> vocabularyTokens CaseListOrder.vocabulary
        | TaskChoiceId.Assignment -> vocabularyTokens CaseAssignment.vocabulary
        | TaskChoiceId.Due -> vocabularyTokens CaseDue.vocabulary
        | TaskChoiceId.ReminderState -> vocabularyTokens ReminderState.vocabulary
        | TaskChoiceId.ReminderDue -> vocabularyTokens ReminderDue.vocabulary
        | TaskChoiceId.RegistrationCorrection ->
            Amendment.actions |> List.map CorrectionGroupAction.token
        | TaskChoiceId.DecisionCorrection -> Change.actions |> List.map CorrectionGroupAction.token

    let vocabularyName =
        function
        | TaskChoiceId.Role
        | TaskChoiceId.RoleFilter -> Vocabulary.name Role.vocabulary
        | TaskChoiceId.PrincipalKind -> Vocabulary.name PrincipalKind.vocabulary
        | TaskChoiceId.Scope -> Vocabulary.name ScopeKind.vocabulary
        | TaskChoiceId.Lifecycle -> Vocabulary.name Lifecycle.vocabulary
        | TaskChoiceId.Status -> Vocabulary.name CaseStatus.vocabulary
        | TaskChoiceId.Progress -> Vocabulary.name CaseProgress.vocabulary
        | TaskChoiceId.CaseView -> Vocabulary.name CaseView.vocabulary
        | TaskChoiceId.ReminderView -> Vocabulary.name ReminderView.vocabulary
        | TaskChoiceId.TimelineFilter -> Vocabulary.name TimelineFilter.vocabulary
        | TaskChoiceId.Order -> Vocabulary.name CaseListOrder.vocabulary
        | TaskChoiceId.Assignment -> Vocabulary.name CaseAssignment.vocabulary
        | TaskChoiceId.Due -> Vocabulary.name CaseDue.vocabulary
        | TaskChoiceId.ReminderState -> Vocabulary.name ReminderState.vocabulary
        | TaskChoiceId.ReminderDue -> Vocabulary.name ReminderDue.vocabulary
        | TaskChoiceId.RegistrationCorrection
        | TaskChoiceId.DecisionCorrection -> Vocabulary.name CorrectionGroupAction.vocabulary

    let ofInput =
        function
        | TaskInputId.Target InputTarget.Role -> Some TaskChoiceId.Role
        | TaskInputId.Target InputTarget.PrincipalKind -> Some TaskChoiceId.PrincipalKind
        | TaskInputId.Scope
        | TaskInputId.Target InputTarget.Scope -> Some TaskChoiceId.Scope
        | TaskInputId.Lifecycle -> Some TaskChoiceId.Lifecycle
        | TaskInputId.Status -> Some TaskChoiceId.Status
        | TaskInputId.Progress -> Some TaskChoiceId.Progress
        | TaskInputId.CaseView -> Some TaskChoiceId.CaseView
        | TaskInputId.RoleFilter -> Some TaskChoiceId.RoleFilter
        | TaskInputId.ReminderView -> Some TaskChoiceId.ReminderView
        | TaskInputId.TimelineFilter -> Some TaskChoiceId.TimelineFilter
        | TaskInputId.Order -> Some TaskChoiceId.Order
        | TaskInputId.Assignment -> Some TaskChoiceId.Assignment
        | TaskInputId.Due -> Some TaskChoiceId.Due
        | TaskInputId.ReminderState -> Some TaskChoiceId.ReminderState
        | TaskInputId.ReminderDue -> Some TaskChoiceId.ReminderDue
        | TaskInputId.Target(InputTarget.Group RecordGroup.Registration) ->
            Some TaskChoiceId.RegistrationCorrection
        | TaskInputId.Target(InputTarget.Group RecordGroup.Decision) ->
            Some TaskChoiceId.DecisionCorrection
        | TaskInputId.Target(InputTarget.Group RecordGroup.Payment)
        | TaskInputId.Target(InputTarget.Field _)
        | TaskInputId.Target(InputTarget.OperationId | InputTarget.ExpectedFamilySequence | InputTarget.ExpectedRevision | InputTarget.ExpectedWorkRevision | InputTarget.ExpectedReminderRevision | InputTarget.Reason | InputTarget.PrincipalIssuer | InputTarget.PrincipalSubject | InputTarget.CaseId | InputTarget.ProfileDisplayName | InputTarget.ProfileContact | InputTarget.DirectorySearch | InputTarget.Grants | InputTarget.Limit | InputTarget.Cursor | InputTarget.Text | InputTarget.ReminderId | InputTarget.NoteId | InputTarget.NoteText | InputTarget.PaymentId | InputTarget.ReminderAction | InputTarget.ReminderDueDate)
        | TaskInputId.ActorId
        | TaskInputId.CaseId
        | TaskInputId.ResponsibleActorFilter
        | TaskInputId.IncludeInactive -> None

type ApplicationTaskDeclaration =
    {
        Task: ApplicationTaskId
        Inputs: TaskInputDeclaration list
        Collections: TaskCollectionDeclaration list
    }

module TaskDeclarations =
    let describe task =
        match task with
        | ApplicationTaskId.Operation act ->
            let declaration =
                ActKind.token act
                |> TaskId.ofLiteral
                |> ClaimCore.Domain.Tasks.Registry.findDeclaration
                |> Option.defaultWith (fun () ->
                    invalidArg (nameof task) "The operation is not declared.")

            {
                Task = task
                Inputs = declaration.Inputs
                Collections = declaration.Collections
            }
        | ApplicationTaskId.Query _
        | ApplicationTaskId.Command _ ->
            let declaration =
                EndpointDeclarations.find (ApplicationTaskId.key task)
                |> Option.defaultWith (fun () ->
                    invalidArg (nameof task) "The endpoint is not declared.")

            {
                Task = task
                Inputs = declaration.Inputs
                Collections = []
            }

    let all = ApplicationTaskId.all |> List.map describe
