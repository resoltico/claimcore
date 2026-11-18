namespace ClaimCore.Domain.Tasks

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs

module internal ReminderRules =
    let planAvailable (state: DecisionFacts option) binding =
        match state, binding with
        | Some state, Binding.Reminder(_, id, _) ->
            Map.tryFind id state.Reminders
            |> Option.forall (fun part -> part.Value.Plan.IsSome)
        | _ -> true

    let plan defaultKind inputs =
        let kind =
            Parsing.optionalText InputTarget.ReminderPlanKind inputs
            |> Result.bind (function
                | None -> Ok defaultKind
                | Some token ->
                    Vocabulary.tryParse ReminderKind.vocabulary token
                    |> Option.map Ok
                    |> Option.defaultValue (
                        Error(Parsing.issue InputTarget.ReminderPlanKind Violation.TextRequired)
                    ))

        Admission.map2
            (fun (action, due) kind ->
                {
                    ReminderPlan.Action = action
                    Due = due
                    Kind = kind
                })
            (Admission.map2
                (fun action due -> action, due)
                (Parsing.scalar InputTarget.ReminderAction ReminderAction.parse inputs)
                (Parsing.scalar InputTarget.ReminderDueDate CalendarDate.parse inputs))
            kind

    let doneOn inputs =
        Parsing.optionalText InputTarget.ReminderDoneOn inputs
        |> Result.bind (function
            | None -> Ok None
            | Some value ->
                CalendarDate.parse value
                |> Parsing.at InputTarget.ReminderDoneOn
                |> Result.map Some)

    let admitDoneOn (context: TaskContext) selected =
        let today =
            context.Decision.BusinessDate
            |> Option.defaultWith (fun () -> invalidOp "Completion requires a business date.")

        defaultArg selected (CalendarDate.ofDate today)
        |> CalendarDate.admit today
        |> Parsing.at InputTarget.ReminderDoneOn

    let doneOnInput =
        Declarations.optional (Declarations.target InputTarget.ReminderDoneOn)

    let planInputs =
        [
            Declarations.required (Declarations.target InputTarget.ReminderAction)
            Declarations.required (Declarations.target InputTarget.ReminderDueDate)
            Declarations.optional (Declarations.target InputTarget.ReminderPlanKind)
        ]

    let identityInputs =
        [
            Declarations.required (Declarations.target InputTarget.ReminderId)
            |> Declarations.bound
            Declarations.required (Declarations.target InputTarget.ExpectedReminderRevision)
        ]

    let current requireOpen allowed (state: DecisionFacts) binding =
        match FamilyRules.active false state with
        | Some refusal -> Error refusal
        | None ->
            match binding with
            | Binding.Reminder(_, id, expected) ->
                match Map.tryFind id state.Reminders with
                | None -> Error FamilyRules.unavailable
                | Some part ->
                    FamilyRules.revision expected part.Revision
                    |> Result.bind (fun () ->
                        match FamilyRules.active requireOpen state with
                        | Some refusal -> Error refusal
                        | None when not (List.contains part.Value.State allowed) ->
                            Error(
                                Refusal.Reminder(ReminderRuleRefusal.NotOffered part.Value.State)
                            )
                        | None -> Ok(id, part))
            | _ -> Error FamilyRules.unavailable

    let availability
        declaration
        (resolve:
            DecisionFacts -> Binding -> Result<ReminderId * AcceptedPart<ReminderValue>, Refusal>)
        environment
        state
        binding
        =
        let offered =
            FamilyRules.availability
                declaration
                environment
                state
                binding
                Capability.ManageCaseWork
                (fun state ->
                    match resolve state binding with
                    | Ok _ -> None
                    | Error refusal -> Some refusal)

        let seeds =
            state
            |> Option.bind (fun state -> resolve state binding |> Result.toOption)
            |> Option.bind (snd >> _.Value.Plan)
            |> Option.map (fun plan ->
                Map.ofList
                    [
                        Declarations.target InputTarget.ReminderAction,
                        InputValue.Text(ReminderAction.format plan.Action)
                        Declarations.target InputTarget.ReminderDueDate,
                        InputValue.Text(CalendarDate.format plan.Due)
                        Declarations.target InputTarget.ReminderPlanKind,
                        InputValue.Text(ReminderKind.token plan.Kind)
                    ])
            |> Option.defaultValue Map.empty
            |> Map.filter (fun input _ ->
                declaration.Inputs
                |> List.exists (fun declared ->
                    declared.Source = InputSource.Authored && declared.Input = input))

        { offered with Seeds = seeds }

    let admitPlan (context: TaskContext) (plan: ReminderPlan) =
        if context.Decision.Lifetime = DecisionLifetime.Recorded then
            Ok plan
        else
            ReminderAction.admit plan.Action
            |> Parsing.at InputTarget.ReminderAction
            |> Result.map (fun _ -> plan)

    let change context (state: DecisionFacts) id revision value =
        FamilyRules.change
            context
            state.Id
            (ReminderId.value id)
            revision
            (PartValue.Reminder value)

    let retained context environment state binding resolve nextState =
        match state with
        | None -> Error(Rejection.Refused FamilyRules.unavailable)
        | Some state ->
            FamilyRules.admit
                context
                environment
                state
                binding
                Capability.ManageCaseWork
                (FamilyRules.active false)
            |> Result.bind (fun () -> resolve state binding)
            |> Result.map (fun (id, before: AcceptedPart<ReminderValue>) ->
                [
                    change
                        context
                        state
                        id
                        (Revision.successor before.Revision)
                        (ReminderValue.Unavailable nextState)
                ])
            |> Parsing.reject
            |> Result.map (TaskContext.changes context)
