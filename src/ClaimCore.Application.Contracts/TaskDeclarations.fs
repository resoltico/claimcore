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
        | TaskInputId.Target(InputTarget.OperationId | InputTarget.ExpectedRevision | InputTarget.ExpectedWorkRevision | InputTarget.ExpectedReminderRevision | InputTarget.Reason | InputTarget.PrincipalIssuer | InputTarget.PrincipalSubject | InputTarget.CaseId | InputTarget.ProfileDisplayName | InputTarget.ProfileContact | InputTarget.DirectorySearch | InputTarget.Grants | InputTarget.Limit | InputTarget.Cursor | InputTarget.Text | InputTarget.ReminderId | InputTarget.NoteId | InputTarget.NoteText | InputTarget.PaymentId | InputTarget.ReminderAction | InputTarget.ReminderDueDate)
        | TaskInputId.ActorId
        | TaskInputId.CaseId
        | TaskInputId.ResponsibleActorFilter
        | TaskInputId.IncludeInactive -> None

[<RequireQualifiedAccess>]
type TaskInputMode =
    | Required
    | Optional
    | Update

module TaskInputMode =
    let vocabulary =
        Vocabulary.ofUnion<TaskInputMode> "inputMode" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type TaskAtom =
    | Text
    | Integer
    | Boolean

/// Value rules remain with the referenced Domain input. This declaration owns task selection only.
type TaskInputDeclaration =
    {
        Input: TaskInputId
        Mode: TaskInputMode
        Source: InputSource
        Clearable: bool
        Atom: TaskAtom
    }

type TaskCollectionDeclaration =
    {
        Collection: TaskCollectionId
        Inputs: TaskInputDeclaration list
    }

type TaskDeclaration =
    {
        Task: TaskId
        Inputs: TaskInputDeclaration list
        Collections: TaskCollectionDeclaration list
    }

module TaskDeclarations =
    let private definition mode input =
        let source =
            match input with
            | TaskInputId.Target InputTarget.OperationId -> InputSource.OperationIdentity
            | TaskInputId.Target(InputTarget.ExpectedRevision | InputTarget.ExpectedWorkRevision | InputTarget.ExpectedReminderRevision) ->
                InputSource.ExpectedRevision
            | _ -> InputSource.Authored

        {
            Input = input
            Mode = mode
            Source = source
            Clearable = input = TaskInputId.Target InputTarget.ProfileContact
            Atom =
                if
                    source = InputSource.ExpectedRevision
                    || input = TaskInputId.Target InputTarget.Limit
                then
                    TaskAtom.Integer
                elif input = TaskInputId.IncludeInactive then
                    TaskAtom.Boolean
                else
                    TaskAtom.Text
        }

    let private required input = definition TaskInputMode.Required input
    let private optional input = definition TaskInputMode.Optional input
    let private target input = TaskInputId.Target input
    let private field input = target (InputTarget.Field input)
    let private operation = required (target InputTarget.OperationId)
    let private caseTarget = required (field BusinessField.CaseReference)
    let private revision = required (target InputTarget.ExpectedRevision)

    let private page =
        [ optional (target InputTarget.Cursor); optional (target InputTarget.Limit) ]

    let private principal =
        [
            InputTarget.PrincipalKind
            InputTarget.PrincipalIssuer
            InputTarget.PrincipalSubject
        ]
        |> List.map (target >> required)

    let private profile =
        [
            required (target InputTarget.ProfileDisplayName)
            optional (target InputTarget.ProfileContact)
        ]

    let private grant =
        [
            required (target InputTarget.Role)
            required TaskInputId.Scope
            optional (field BusinessField.CaseReference)
            optional TaskInputId.CaseId
        ]

    let private group mode group =
        BusinessField.ofGroup group
        |> List.map (fun input ->
            let selected =
                if
                    mode = TaskInputMode.Required && (BusinessField.describe input).AllowsAbsence
                then
                    TaskInputMode.Optional
                else
                    mode

            { definition selected (field input) with
                Clearable = (BusinessField.describe input).AllowsAbsence
            })

    let private worklist =
        [
            TaskInputId.Lifecycle
            TaskInputId.Status
            TaskInputId.Progress
            TaskInputId.CaseView
            TaskInputId.ResponsibleActorFilter
            TaskInputId.Assignment
            TaskInputId.Due
            TaskInputId.Order
            target InputTarget.Text
        ]
        |> List.map optional

    let describe task =
        let inputs, collections =
            match task with
            | TaskId.Operation(ActKind.Case kind) ->
                let values =
                    match kind with
                    | CaseActKind.Open -> group TaskInputMode.Required RecordGroup.Registration
                    | CaseActKind.AmendRegistration ->
                        group TaskInputMode.Update RecordGroup.Registration
                    | CaseActKind.Decide -> group TaskInputMode.Required RecordGroup.Decision
                    | CaseActKind.RecordPayment -> group TaskInputMode.Required RecordGroup.Payment
                    | CaseActKind.RemovePayment -> [ required (target InputTarget.PaymentId) ]
                    | CaseActKind.Correct ->
                        [
                            required (target (InputTarget.Group RecordGroup.Registration))
                            required (target (InputTarget.Group RecordGroup.Decision))
                        ]
                        @ group TaskInputMode.Update RecordGroup.Registration
                        @ group TaskInputMode.Update RecordGroup.Decision
                    | CaseActKind.Void
                    | CaseActKind.Reinstate -> [ required (target InputTarget.Reason) ]
                    | CaseActKind.WithdrawDecision
                    | CaseActKind.Close
                    | CaseActKind.Reopen
                    | CaseActKind.Erase -> []

                operation :: caseTarget :: revision :: values, []
            | TaskId.Operation(ActKind.People kind) ->
                let values, collections =
                    match kind with
                    | PeopleActKind.Register
                    | PeopleActKind.SetProfile -> profile, []
                    | PeopleActKind.Admit ->
                        profile,
                        [
                            {
                                Collection = TaskCollectionId.PeopleGrants
                                Inputs = grant
                            }
                        ]
                    | PeopleActKind.Grant
                    | PeopleActKind.Revoke -> grant, []
                    | PeopleActKind.Enable
                    | PeopleActKind.Disable
                    | PeopleActKind.EraseProfile -> [], []
                    | PeopleActKind.ProvisionOwner ->
                        invalidArg (nameof task) "Owner provisioning is an operator task."

                let identity =
                    if kind = PeopleActKind.Register || kind = PeopleActKind.Admit then
                        principal
                    else
                        [ required TaskInputId.ActorId ]

                operation :: (identity @ values), collections
            | TaskId.Operation(ActKind.Work WorkActKind.UpdateWork) ->
                [
                    operation
                    caseTarget
                    required (target InputTarget.ExpectedWorkRevision)
                    optional (field BusinessField.ResponsibleActor)
                    optional (field BusinessField.HandlingSummary)
                ],
                []
            | TaskId.Operation(ActKind.Note kind) ->
                let input =
                    match kind with
                    | NoteActKind.AddNote -> InputTarget.NoteText
                    | NoteActKind.RemoveNote -> InputTarget.NoteId

                [ operation; caseTarget; required (target input) ], []
            | TaskId.Operation(ActKind.Reminder kind) ->
                let identity =
                    [
                        required (target InputTarget.ReminderId)
                        required (target InputTarget.ExpectedReminderRevision)
                    ]

                let plan =
                    [
                        required (target InputTarget.ReminderAction)
                        required (target InputTarget.ReminderDueDate)
                    ]

                let values =
                    match kind with
                    | ReminderActKind.Create -> plan
                    | ReminderActKind.Update
                    | ReminderActKind.CompleteAndPlanNext -> identity @ plan
                    | ReminderActKind.Reopen ->
                        identity @ [ required (target InputTarget.ReminderDueDate) ]
                    | ReminderActKind.Complete
                    | ReminderActKind.Cancel -> identity

                operation :: caseTarget :: values, []
            | TaskId.Command CommandTaskId.PeopleRequestsDismiss -> principal, []
            | TaskId.Query query ->
                let values =
                    match query with
                    | QueryTaskId.CaseGet -> [ caseTarget ]
                    | QueryTaskId.CaseAssignees
                    | QueryTaskId.CaseNotes
                    | QueryTaskId.CasePayments -> caseTarget :: page
                    | QueryTaskId.CaseTimeline ->
                        caseTarget :: optional TaskInputId.TimelineFilter :: page
                    | QueryTaskId.CaseList -> worklist @ page
                    | QueryTaskId.CaseSummary -> worklist
                    | QueryTaskId.ReminderList ->
                        [
                            optional TaskInputId.ReminderView
                            optional (field BusinessField.CaseReference)
                            optional TaskInputId.ReminderState
                            optional TaskInputId.ReminderDue
                            optional TaskInputId.Assignment
                            optional (target InputTarget.Text)
                            optional TaskInputId.IncludeInactive
                        ]
                        @ page
                    | QueryTaskId.ReminderGet ->
                        [ caseTarget; required (target InputTarget.ReminderId) ]
                    | QueryTaskId.OperationGet -> [ required (target InputTarget.OperationId) ]
                    | QueryTaskId.PeopleList ->
                        [
                            optional (target InputTarget.DirectorySearch)
                            optional TaskInputId.RoleFilter
                        ]
                        @ page
                    | QueryTaskId.PeopleGrants -> required TaskInputId.ActorId :: page
                    | QueryTaskId.PeopleHistory -> optional TaskInputId.ActorId :: page
                    | QueryTaskId.PeopleRequests -> page
                    | QueryTaskId.WorkViews
                    | QueryTaskId.Context -> []

                values, []

        {
            Task = task
            Inputs =
                match task with
                | TaskId.Query _
                | TaskId.Command _ ->
                    inputs
                    |> List.map (fun input ->
                        { input with
                            Source = InputSource.Authored
                        })
                | TaskId.Operation kind ->
                    inputs
                    |> List.map (fun input ->
                        let binding =
                            match kind, input.Input with
                            | (ActKind.Case _ | ActKind.Work _ | ActKind.Note _ | ActKind.Reminder _),
                              TaskInputId.Target(InputTarget.Field BusinessField.CaseReference) ->
                                true
                            | ActKind.Case _, TaskInputId.Target InputTarget.PaymentId
                            | ActKind.Note _, TaskInputId.Target InputTarget.NoteId
                            | ActKind.Reminder _, TaskInputId.Target InputTarget.ReminderId
                            | ActKind.People _, TaskInputId.ActorId -> true
                            | _ -> false

                        let clearable =
                            match kind, input.Input with
                            | ActKind.Work _,
                              TaskInputId.Target(InputTarget.Field(BusinessField.ResponsibleActor | BusinessField.HandlingSummary)) ->
                                true
                            | _ -> input.Clearable

                        { input with
                            Source = (if binding then InputSource.Binding else input.Source)
                            Clearable = clearable
                            Mode =
                                if kind = ActKind.Work WorkActKind.UpdateWork && clearable then
                                    TaskInputMode.Update
                                else
                                    input.Mode
                        })
            Collections = collections
        }

    let all = TaskId.all |> List.map describe
