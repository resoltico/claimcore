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

    let caseAction kind id revision seeds editable choices =
        let task = TaskId.Operation(ActKind.Case kind)

        offered
            task
            (OperationBinding.Case(
                CaseSelector.Id id,
                (if kind = CaseActKind.Open then
                     0L
                 else
                     Revision.value revision),
                None
            ))
            seeds
            editable
            choices

    let noteAdd id =
        let task = TaskId.Operation(ActKind.Note NoteActKind.AddNote)

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
                    (TaskId.Operation(ActKind.Note NoteActKind.RemoveNote))
                    (OperationBinding.Note(CaseSelector.Id id, Some(NoteId.value item.NoteId)))
                    Map.empty
                    []
                    Map.empty
            ]
        else
            []

    let paymentRemoveEntry id revision entry =
        offered
            (TaskId.Operation(ActKind.Case CaseActKind.RemovePayment))
            (OperationBinding.Case(
                CaseSelector.Id id,
                Revision.value revision,
                Some(OperationId.value (PaymentEntry.id entry))
            ))
            Map.empty
            []
            Map.empty

    let paymentRemove id revision (item: CasePaymentItem) =
        if item.CanRemove then
            [
                offered
                    (TaskId.Operation(ActKind.Case CaseActKind.RemovePayment))
                    (OperationBinding.Case(
                        CaseSelector.Id id,
                        Revision.value revision,
                        Some(OperationId.value (PaymentEntry.id item.Entry))
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
            let task = TaskId.Operation(ActKind.Reminder kind)

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
        let task = TaskId.Operation(ActKind.Reminder ReminderActKind.Create)

        offered
            task
            (OperationBinding.Reminder(CaseSelector.Id id, None))
            Map.empty
            (authored task)
            Map.empty

    let work id revision seeds editable =
        offered
            (TaskId.Operation(ActKind.Work WorkActKind.UpdateWork))
            (OperationBinding.Work(CaseSelector.Id id, Revision.value revision))
            seeds
            editable
            Map.empty

    let person kind actor seeds editable choices =
        offered
            (TaskId.Operation(ActKind.People kind))
            (OperationBinding.People(Some(ActorId.value actor)))
            seeds
            editable
            choices

    let initialGrantRows =
        SubmissionValue.Rows [ Map.ofList [ TaskInputId.Scope, text "INSTALLATION" ] ]

    let grantSeeds role scope =
        [
            input InputTarget.Role, text (Role.token role)
            TaskInputId.Scope,
            text (
                match scope with
                | GrantScope.Installation -> "INSTALLATION"
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
                (TaskId.Operation(ActKind.People PeopleActKind.Admit))
                (OperationBinding.People None)
                seeds
                [
                    input InputTarget.ProfileDisplayName
                    input InputTarget.ProfileContact
                    input InputTarget.Grants
                ]
                Map.empty

        let dismiss =
            offered
                (TaskId.Command CommandTaskId.PeopleRequestsDismiss)
                (OperationBinding.People None)
                (principal value.Principal)
                []
                Map.empty

        [ admit; dismiss ]
