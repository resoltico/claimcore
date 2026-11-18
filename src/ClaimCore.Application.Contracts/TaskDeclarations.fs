namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type TaskChoiceId =
    | Role
    | PartyRole
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
    | PaymentDirection
    | ClosureOutcome
    | ReminderKind
    | ReminderKindFilter
    | DeadlineOutcomeFilter
    | RegistrationCorrection
    | DecisionCorrection
    | GroupBy

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
        | TaskChoiceId.PartyRole -> vocabularyTokens PartyRole.vocabulary
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
        | TaskChoiceId.GroupBy -> vocabularyTokens CaseGrouping.vocabulary
        | TaskChoiceId.ReminderState -> vocabularyTokens ReminderStateSelection.vocabulary
        | TaskChoiceId.ReminderKind -> vocabularyTokens ReminderKind.vocabulary
        | TaskChoiceId.DeadlineOutcomeFilter -> vocabularyTokens DeadlineOutcome.vocabulary
        | TaskChoiceId.ReminderKindFilter -> vocabularyTokens ReminderKindSelection.vocabulary
        | TaskChoiceId.ReminderDue -> vocabularyTokens ReminderDue.vocabulary
        | TaskChoiceId.PaymentDirection -> vocabularyTokens PaymentDirection.vocabulary
        | TaskChoiceId.ClosureOutcome -> vocabularyTokens ClosureOutcome.vocabulary
        | TaskChoiceId.RegistrationCorrection ->
            CorrectionGroupAction.actions RecordGroup.Registration
            |> List.map CorrectionGroupAction.token
        | TaskChoiceId.DecisionCorrection ->
            CorrectionGroupAction.actions RecordGroup.Decision
            |> List.map CorrectionGroupAction.token

    let vocabularyName =
        function
        | TaskChoiceId.Role
        | TaskChoiceId.RoleFilter -> Vocabulary.name Role.vocabulary
        | TaskChoiceId.PartyRole -> Vocabulary.name PartyRole.vocabulary
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
        | TaskChoiceId.GroupBy -> Vocabulary.name CaseGrouping.vocabulary
        | TaskChoiceId.ReminderState -> Vocabulary.name ReminderStateSelection.vocabulary
        | TaskChoiceId.ReminderKind -> Vocabulary.name ReminderKind.vocabulary
        | TaskChoiceId.DeadlineOutcomeFilter -> Vocabulary.name DeadlineOutcome.vocabulary
        | TaskChoiceId.ReminderKindFilter -> Vocabulary.name ReminderKindSelection.vocabulary
        | TaskChoiceId.ReminderDue -> Vocabulary.name ReminderDue.vocabulary
        | TaskChoiceId.PaymentDirection -> Vocabulary.name PaymentDirection.vocabulary
        | TaskChoiceId.ClosureOutcome -> Vocabulary.name ClosureOutcome.vocabulary
        | TaskChoiceId.RegistrationCorrection
        | TaskChoiceId.DecisionCorrection -> Vocabulary.name CorrectionGroupAction.vocabulary

    let ofInput =
        function
        | TaskInputId.Target InputTarget.PaymentDirection -> Some TaskChoiceId.PaymentDirection
        | TaskInputId.Target InputTarget.Role -> Some TaskChoiceId.Role
        | TaskInputId.Target InputTarget.PartyRole -> Some TaskChoiceId.PartyRole
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
        | TaskInputId.GroupBy -> Some TaskChoiceId.GroupBy
        | TaskInputId.ReminderState -> Some TaskChoiceId.ReminderState
        | TaskInputId.Target InputTarget.ReminderPlanKind -> Some TaskChoiceId.ReminderKind
        | TaskInputId.DeadlineOutcomeFilter -> Some TaskChoiceId.DeadlineOutcomeFilter
        | TaskInputId.ReminderKindFilter -> Some TaskChoiceId.ReminderKindFilter
        | TaskInputId.ReminderDue -> Some TaskChoiceId.ReminderDue
        | TaskInputId.Target(InputTarget.Group RecordGroup.Registration) ->
            Some TaskChoiceId.RegistrationCorrection
        | TaskInputId.Target(InputTarget.Group RecordGroup.Decision) ->
            Some TaskChoiceId.DecisionCorrection
        | TaskInputId.Target(InputTarget.Group RecordGroup.Payment)
        | TaskInputId.Target(InputTarget.Field BusinessField.Outcome) ->
            Some TaskChoiceId.ClosureOutcome
        | TaskInputId.Target(InputTarget.Field _)
        | TaskInputId.Target(InputTarget.OperationId | InputTarget.ExpectedFamilySequence | InputTarget.ExpectedRevision | InputTarget.ExpectedWorkRevision | InputTarget.ExpectedReminderRevision | InputTarget.Reason | InputTarget.PrincipalIssuer | InputTarget.PrincipalSubject | InputTarget.CaseId | InputTarget.ProfileDisplayName | InputTarget.ProfileContact | InputTarget.ProfileDigestEmail | InputTarget.DirectorySearch | InputTarget.Grants | InputTarget.Limit | InputTarget.Cursor | InputTarget.Text | InputTarget.ReminderId | InputTarget.NoteId | InputTarget.NoteText | InputTarget.Parties | InputTarget.PartyId | InputTarget.PartyName | InputTarget.PartyDetails | InputTarget.ExpectedPartyRevision | InputTarget.PaymentId | InputTarget.PaymentReference | InputTarget.PaymentPayee | InputTarget.ReminderAction | InputTarget.ReminderDueDate | InputTarget.NewCaseReference | InputTarget.LinkId | InputTarget.LinkTargetReference | InputTarget.LinkTargetId | InputTarget.LinkNote | InputTarget.ReminderDoneOn)
        | TaskInputId.ActorId
        | TaskInputId.CaseId
        | TaskInputId.ResponsibleActorFilter
        | TaskInputId.CaseDate _
        | TaskInputId.IncludeInactive -> None

[<RequireQualifiedAccess>]
type ClientTaskKind =
    | Operation
    | Query
    | Command

/// A derived client view of the separate ledger and endpoint declarations.
type ClientTaskDeclaration =
    private
        {
            IdValue: string
            KindValue: ClientTaskKind
            InputsValue: TaskInputDeclaration list
            CollectionsValue: TaskCollectionDeclaration list
            LedgerValue: TaskDeclaration option
            EndpointValue: EndpointDeclaration option
        }

    member this.Id = this.IdValue
    member this.Kind = this.KindValue
    member this.Inputs = this.InputsValue
    member this.Collections = this.CollectionsValue
    member this.Ledger = this.LedgerValue
    member this.Endpoint = this.EndpointValue
    member this.Creates = this.LedgerValue |> Option.exists _.Creates

    member this.Consequence =
        this.LedgerValue
        |> Option.map _.Consequence
        |> Option.defaultValue Consequence.Routine

    member this.Subject = this.LedgerValue |> Option.map _.Target
    member this.Paged = this.EndpointValue |> Option.exists _.Paged
    member this.Csv = this.EndpointValue |> Option.exists _.Csv

module TaskDeclarations =
    let all =
        (ClaimCore.Domain.Tasks.Registry.clientDeclarations
         |> List.map (fun declaration ->
             {
                 IdValue = TaskId.token declaration.Id
                 KindValue = ClientTaskKind.Operation
                 InputsValue = declaration.Inputs
                 CollectionsValue = declaration.Collections
                 LedgerValue = Some declaration
                 EndpointValue = None
             }))
        @ (EndpointDeclarations.all
           |> List.map (fun declaration ->
               {
                   IdValue = declaration.Id
                   KindValue =
                       (if declaration.Kind = EndpointKind.Query then
                            ClientTaskKind.Query
                        else
                            ClientTaskKind.Command)
                   InputsValue = declaration.Inputs
                   CollectionsValue = []
                   LedgerValue = None
                   EndpointValue = Some declaration
               }))

    let private byId =
        all |> List.map (fun declaration -> declaration.Id, declaration) |> Map.ofList

    let find id = Map.tryFind id byId

    let describe id =
        find id
        |> Option.defaultWith (fun () -> invalidArg (nameof id) "The client task is not declared.")
