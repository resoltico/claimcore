namespace ClaimCore.Domain.Tasks

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs

module internal ReminderRules =
    let planAvailable (state: CaseFamily option) binding =
        match state, binding with
        | Some state, Binding.Reminder(_, id, _) ->
            Map.tryFind id state.Reminders
            |> Option.forall (fun part -> part.Value.Plan.IsSome)
        | _ -> true

    let plan inputs =
        Admission.map2
            (fun action due ->
                {
                    ReminderPlan.Action = action
                    Due = due
                })
            (Parsing.scalar InputTarget.ReminderAction ReminderAction.parse inputs)
            (Parsing.scalar InputTarget.ReminderDueDate CalendarDate.parse inputs)

    let planInputs =
        [
            Declarations.required (Declarations.target InputTarget.ReminderAction)
            Declarations.required (Declarations.target InputTarget.ReminderDueDate)
        ]

    let identityInputs =
        [
            Declarations.required (Declarations.target InputTarget.ReminderId)
            |> Declarations.bound
            Declarations.required (Declarations.target InputTarget.ExpectedReminderRevision)
        ]

    let current requireOpen allowed (state: CaseFamily) binding =
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

    let gate requireOpen allowed binding state =
        match current requireOpen allowed state binding with
        | Ok _ -> None
        | Error refusal -> Some refusal

    let availability declaration requireOpen allowed environment state binding =
        let offered =
            FamilyRules.availability
                declaration
                environment
                state
                binding
                Capability.ManageCaseWork
                (gate requireOpen allowed binding)

        let seeds =
            state
            |> Option.bind (fun state ->
                current requireOpen allowed state binding |> Result.toOption)
            |> Option.bind (snd >> _.Value.Plan)
            |> Option.map (fun plan ->
                Map.ofList
                    [
                        Declarations.target InputTarget.ReminderAction,
                        InputValue.Text(ReminderAction.format plan.Action)
                        Declarations.target InputTarget.ReminderDueDate,
                        InputValue.Text(CalendarDate.format plan.Due)
                    ])
            |> Option.defaultValue Map.empty

        { offered with Seeds = seeds }

    let admitPlan (context: TaskContext) (plan: ReminderPlan) =
        if context.Decision.Lifetime = DecisionLifetime.Recorded then
            Ok plan
        else
            ReminderAction.admit plan.Action
            |> Parsing.at InputTarget.ReminderAction
            |> Result.map (fun _ -> plan)

    let change context (state: CaseFamily) id revision value =
        FamilyRules.change
            context
            state.Id
            (ReminderId.value id)
            revision
            (PartValue.Reminder value)
