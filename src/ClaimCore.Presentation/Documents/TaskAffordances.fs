namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Core supplies selection, fixed values and editable facts together.
module TaskAffordances =
    let input value = TaskInputId.Target value
    let field value = input (InputTarget.Field value)
    let text value = SubmissionValue.Text value

    let fields values =
        values |> List.map (fun (name, value) -> field name, text value) |> Map.ofList

    let principal (value: PrincipalKey) =
        let value = PrincipalKey.toInput value

        Map.ofList
            [
                input InputTarget.PrincipalKind,
                text (Vocabulary.token PrincipalKind.vocabulary value.Kind)
                input InputTarget.PrincipalIssuer, text value.Issuer
                input InputTarget.PrincipalSubject, text value.Subject
            ]

    let profile (value: ActorProfileInput) =
        [
            input InputTarget.ProfileDisplayName, text value.DisplayName
            match value.Contact with
            | Some contact -> input InputTarget.ProfileContact, text contact
            | None -> ()
        ]
        |> Map.ofList

    let authored task =
        let declaration = TaskDeclarations.describe task

        (declaration.Inputs
         |> List.filter (fun input -> input.Source = InputSource.Authored)
         |> List.map _.Input)
        @ (declaration.Collections
           |> List.map (fun collection -> TaskCollectionId.input collection.Collection))

    let offered task binding seeds editable choices =
        Affordance.create task binding seeds editable choices false None

    let caseAction kind id revision family seeds editable choices =
        let task = ApplicationTaskId.Operation(ActKind.Case kind)

        offered
            task
            (if (TaskConsequences.declaration (ActKind.Case kind)).Creates then
                 OperationBinding.CaseNew 0L
             else
                 OperationBinding.Case(
                     CaseSelector.Id id,
                     (if kind = CaseActKind.Open then
                          0L
                      else
                          Revision.value revision),
                     None,
                     (if kind = CaseActKind.Erase then
                          Some(Sequence.value family)
                      else
                          None)
                 ))
            seeds
            editable
            choices

    let noteAdd id =
        let task = ApplicationTaskId.Operation(ActKind.Note NoteActKind.AddNote)

        offered
            task
            (OperationBinding.Note(CaseSelector.Id id, None))
            Map.empty
            (authored task)
            Map.empty

    let noteRemove id (item: NoteReadModel) =
        if item.CanRemove then
            [
                offered
                    (ApplicationTaskId.Operation(ActKind.Note NoteActKind.RemoveNote))
                    (OperationBinding.Note(CaseSelector.Id id, Some(NoteId.value item.NoteId)))
                    Map.empty
                    []
                    Map.empty
            ]
        else
            []

    let paymentRemoveEntry id revision entry =
        offered
            (ApplicationTaskId.Operation(ActKind.Case CaseActKind.RemovePayment))
            (OperationBinding.Case(
                CaseSelector.Id id,
                Revision.value revision,
                Some(OperationId.value (PaymentEntry.id entry)),
                None
            ))
            Map.empty
            []
            Map.empty

    let paymentRemove id revision (item: CasePaymentItem) =
        if item.CanRemove then
            [
                offered
                    (ApplicationTaskId.Operation(ActKind.Case CaseActKind.RemovePayment))
                    (OperationBinding.Case(
                        CaseSelector.Id id,
                        Revision.value revision,
                        Some(OperationId.value (PaymentEntry.id item.Entry)),
                        None
                    ))
                    Map.empty
                    []
                    Map.empty
            ]
        else
            []

    let reminder (value: ReminderReadModel) =
        value.Acts
        |> List.map (fun kind ->
            let task = ApplicationTaskId.Operation(ActKind.Reminder kind)

            let seeds =
                [
                    if kind = ReminderActKind.Update then
                        input InputTarget.ReminderAction,
                        text (ReminderAction.format (Reminder.action value.Reminder))
                    if kind = ReminderActKind.Update || kind = ReminderActKind.Reopen then
                        input InputTarget.ReminderDueDate,
                        text (CalendarDate.format (Reminder.due value.Reminder))
                ]
                |> Map.ofList

            let current =
                Some(
                    ReminderId.value (Reminder.id value.Reminder),
                    Revision.value (Reminder.revision value.Reminder)
                )

            let binding =
                OperationBinding.Reminder(CaseSelector.Id(Reminder.caseId value.Reminder), current)

            offered task binding seeds (authored task) Map.empty)

    let reminderCreate id =
        let task = ApplicationTaskId.Operation(ActKind.Reminder ReminderActKind.Create)

        offered
            task
            (OperationBinding.Reminder(CaseSelector.Id id, None))
            Map.empty
            (authored task)
            Map.empty

    let work id revision seeds editable =
        offered
            (ApplicationTaskId.Operation(ActKind.Work WorkActKind.UpdateWork))
            (OperationBinding.Work(CaseSelector.Id id, Revision.value revision))
            seeds
            editable
            Map.empty

    let person kind actor revision seeds editable choices =
        offered
            (ApplicationTaskId.Operation(ActKind.People kind))
            (OperationBinding.People(Some(ActorId.value actor), Revision.value revision))
            seeds
            editable
            choices

    let collectionDefaults collection =
        TaskCollectionId.defaultValues collection
        |> Map.map (fun _ value ->
            match value with
            | InputValue.Text value -> SubmissionValue.Text value
            | InputValue.Integer value -> SubmissionValue.Integer value
            | InputValue.Boolean value -> SubmissionValue.Boolean value
            | InputValue.Null -> SubmissionValue.Clear)

    let initialGrantScope =
        (collectionDefaults TaskCollectionId.PeopleGrants)[TaskInputId.Scope]

    let initialGrantRows =
        collectionDefaults TaskCollectionId.PeopleGrants
        |> List.replicate (TaskCollectionId.minimumRows TaskCollectionId.PeopleGrants)
        |> SubmissionValue.Rows

    let grantSeeds role scope =
        [
            input InputTarget.Role, text (Role.token role)
            TaskInputId.Scope,
            text (
                match scope with
                | GrantScope.Installation -> ScopeKind.token ScopeKind.Installation
                | _ -> "CASE"
            )
            match scope with
            | GrantScope.Case(_, reference) ->
                field BusinessField.CaseReference, text (CaseReference.format reference)
            | _ -> ()
        ]
        |> Map.ofList

    let availability refusal =
        let message, args =
            match refusal with
            | Refusal.Access AccessRefusal.ResourceUnavailable -> "RESOURCE_UNAVAILABLE", Map.empty
            | Refusal.Access(AccessRefusal.NotAuthorized capability) ->
                "NOT_AUTHORIZED",
                Map.ofList
                    [
                        "required",
                        PresentedValue.Codes(
                            Vocabulary.name Role.vocabulary,
                            Access.rolesFor capability |> List.map Role.token
                        )
                    ]
            | Refusal.Case(CaseRefusal.NotOffered state) ->
                "NOT_OFFERED", Map.ofList [ "state", PresentedValue.State state ]
            | Refusal.Case CaseRefusal.CaseAlreadyExists -> "CASE_ALREADY_EXISTS", Map.empty
            | Refusal.RegisterMaintenance -> "REGISTER_MAINTENANCE", Map.empty
            | _ ->
                invalidArg
                    (nameof refusal)
                    "Case availability needs a permission, state or maintenance reason."

        {
            HumanAdvice.Message =
                {
                    MessageId = "diagnostic." + message
                    Arguments = args
                }
            Action = Refusals.action refusal
        }

    let requestActions (value: AccessRequest) =
        let hints =
            {
                ActorProfileInput.DisplayName =
                    value.Claims.Name
                    |> Option.orElse value.Claims.PreferredUsername
                    |> Option.map PartyName.format
                    |> Option.defaultValue ""
                Contact = value.Claims.Email |> Option.map PartyName.format
            }

        let seeds =
            Map.fold
                (fun values input value -> Map.add input value values)
                (principal value.Principal)
                (profile hints)

        let seeds = seeds |> Map.add (input InputTarget.Grants) initialGrantRows

        let admit =
            offered
                (ApplicationTaskId.Operation(ActKind.People PeopleActKind.Admit))
                (OperationBinding.People(None, 0L))
                seeds
                [
                    input InputTarget.ProfileDisplayName
                    input InputTarget.ProfileContact
                    input InputTarget.Grants
                ]
                Map.empty

        let dismiss =
            offered
                (ApplicationTaskId.Command CommandTaskId.PeopleRequestsDismiss)
                (OperationBinding.People(None, 0L))
                (principal value.Principal)
                []
                Map.empty

        [ admit; dismiss ]
