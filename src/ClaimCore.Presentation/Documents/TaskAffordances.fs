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

    let private binding task selection revision identity family =
        SubmissionAssembly.offeredBinding
            (TaskConsequences.declaration task)
            selection
            revision
            identity
            family

    let caseAction task id revision family seeds editable choices =
        offered
            task
            (Some(binding task (Some(CaseSelector.Id id)) (Some revision) None (Some family)))
            seeds
            editable
            choices

    let noteAdd id =
        let task = ClientCompositions.noteEntry

        offered
            task
            (Some(binding task (Some(CaseSelector.Id id)) None None None))
            Map.empty
            (authored task)
            Map.empty

    let noteRemove id (item: NoteReadModel) =
        if item.CanRemove then
            [
                offered
                    (ClientCompositions.noteRemoval)
                    (Some(
                        binding
                            ClientCompositions.noteRemoval
                            (Some(CaseSelector.Id id))
                            None
                            (Some(NoteId.value item.NoteId))
                            None
                    ))
                    Map.empty
                    []
                    Map.empty
            ]
        else
            []

    let paymentRemoveEntry id revision entry =
        offered
            (ClientCompositions.paymentRemoval)
            (Some(
                binding
                    ClientCompositions.paymentRemoval
                    (Some(CaseSelector.Id id))
                    (Some revision)
                    (Some(OperationId.value (PaymentEntry.id entry)))
                    None
            ))
            Map.empty
            []
            Map.empty

    let paymentCorrectionEntry id revision entry =
        let task = ClientCompositions.paymentCorrection

        offered
            task
            (Some(
                binding
                    task
                    (Some(CaseSelector.Id id))
                    (Some revision)
                    (Some(OperationId.value (PaymentEntry.id entry)))
                    None
            ))
            (Map.ofList
                [
                    field BusinessField.PaymentDate,
                    text (CalendarDate.format (PaymentEntry.date entry))
                    field BusinessField.PaidAmount, text (Amount.format (PaymentEntry.amount entry))
                    field BusinessField.PaidCurrency,
                    text (Money.currency (PaymentEntry.money entry))
                    TaskInputId.Target InputTarget.PaymentDirection,
                    text (PaymentDirection.token (PaymentEntry.direction entry))
                    match PaymentEntry.reference entry with
                    | Some reference ->
                        TaskInputId.Target InputTarget.PaymentReference,
                        text (Identifier.format reference)
                    | None -> ()
                    match PaymentEntry.payee entry with
                    | Some payee ->
                        TaskInputId.Target InputTarget.PaymentPayee, text (PartyId.format payee)
                    | None -> ()
                ])
            (authored task)
            Map.empty

    let paymentEntryActions id revision (item: CasePaymentItem) =
        item.Actions
        |> List.map (fun task ->
            if task = ClientCompositions.paymentRemoval then
                paymentRemoveEntry id revision item.Entry
            elif task = ClientCompositions.paymentCorrection then
                paymentCorrectionEntry id revision item.Entry
            else
                invalidArg "item" "A payment item contains an undeclared action.")

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
        let task = ClientCompositions.reminderCreation

        offered
            task
            (Some(binding task (Some(CaseSelector.Id id)) None None None))
            Map.empty
            (authored task)
            Map.empty

    let work id revision seeds editable =
        let action =
            offered
                ClientCompositions.assignment
                (Some(
                    binding
                        ClientCompositions.assignment
                        (Some(CaseSelector.Id id))
                        (Some revision)
                        None
                        None
                ))
                seeds
                editable
                Map.empty

        { action with
            LabelId = "label.assignmentAction"
        }

    let person task actor revision seeds editable choices =
        offered
            task
            (Some(binding task None (Some revision) (Some(ActorId.value actor)) None))
            seeds
            editable
            choices

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

        let declaration = TaskConsequences.declaration ClientCompositions.admission

        let editable =
            authored declaration.Id
            |> List.filter (fun input -> not (Map.containsKey input (principal value.Principal)))

        let admit =
            offered
                declaration.Id
                (Some(binding declaration.Id None None None None))
                seeds
                editable
                Map.empty

        let dismiss =
            Affordance.command (CommandRequest.DismissAccessRequest value.Principal)

        [ admit; dismiss ]
