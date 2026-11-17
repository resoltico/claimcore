namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Core supplies selection, fixed values and editable facts together.
module TaskAffordances =
    let input value = TaskInputId.Target value
    let field value = input (InputTarget.Field value)
    let text value = InputValue.Text value

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

    let private definition task =
        TaskDeclarations.describe (TaskId.token task)

    let authored task =
        let declaration = definition task

        (declaration.Inputs
         |> List.filter (fun input -> input.Source = InputSource.Authored)
         |> List.map _.Input)
        @ (declaration.Collections
           |> List.map (fun collection -> TaskCollectionId.input collection.Collection))

    let offered task binding seeds editable choices =
        Affordance.create task binding seeds editable choices None

    let private selected shape =
        ClaimCore.Domain.Tasks.Registry.clientDeclarations
        |> List.find (fun declaration -> TaskDeclaration.bindingShape declaration = shape)
        |> _.Id

    let caseAction task id revision family seeds editable choices =
        let declaration = TaskConsequences.declaration task

        let binding =
            match TaskDeclaration.bindingShape declaration with
            | BindingShape.CaseNew -> Binding.CaseNew Revision.zero
            | BindingShape.CaseErase -> Binding.CaseErase(CaseSelector.Id id, revision, family)
            | BindingShape.Case -> Binding.Case(CaseSelector.Id id, revision)
            | _ -> invalidArg (nameof task) "A row selection is required for this action."

        offered task (Some binding) seeds editable choices

    let noteAdd id =
        let task = selected BindingShape.NoteNew
        offered task (Some(Binding.NoteNew(CaseSelector.Id id))) Map.empty (authored task) Map.empty

    let noteRemove id (item: NoteReadModel) =
        if item.CanRemove then
            [
                offered
                    (selected BindingShape.Note)
                    (Some(Binding.Note(CaseSelector.Id id, item.NoteId)))
                    Map.empty
                    []
                    Map.empty
            ]
        else
            []

    let paymentRemoveEntry id revision entry =
        offered
            (selected BindingShape.CasePayment)
            (Some(Binding.CasePayment(CaseSelector.Id id, revision, PaymentEntry.id entry)))
            Map.empty
            []
            Map.empty

    let paymentRemove id revision (item: CasePaymentItem) =
        if item.CanRemove then
            [ paymentRemoveEntry id revision item.Entry ]
        else
            []

    let fromOffer =
        function
        | TaskOffer.Declared _ -> None
        | TaskOffer.Available(task, binding, availability) when availability.Blocked.IsNone ->
            Some(
                offered
                    task
                    (Some binding)
                    availability.Seeds
                    availability.Editable
                    availability.Choices
            )
        | TaskOffer.Available _ -> None

    let reminder (value: ReminderReadModel) = value.Offers |> List.choose fromOffer

    let reminderCreate id =
        let task = selected BindingShape.ReminderNew

        offered
            task
            (Some(Binding.ReminderNew(CaseSelector.Id id)))
            Map.empty
            (authored task)
            Map.empty

    let work id revision seeds editable =
        offered
            (selected BindingShape.Work)
            (Some(Binding.Work(CaseSelector.Id id, revision)))
            seeds
            editable
            Map.empty

    let person task actor revision seeds editable choices =
        offered task (Some(Binding.People(actor, revision))) seeds editable choices

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

        let declaration =
            ClaimCore.Domain.Tasks.Registry.clientDeclarations
            |> List.find (fun declaration ->
                declaration.Target = ActSubject.People
                && declaration.Creates
                && not declaration.Collections.IsEmpty)

        let editable =
            authored declaration.Id
            |> List.filter (fun input -> not (Map.containsKey input (principal value.Principal)))

        let admit =
            offered declaration.Id (Some(Binding.PeopleNew Revision.zero)) seeds editable Map.empty

        let command = EndpointDeclarations.commands |> List.exactlyOne

        let dismiss =
            offered (TaskId.ofLiteral command.Id) None (principal value.Principal) [] Map.empty

        [ admit; dismiss ]
