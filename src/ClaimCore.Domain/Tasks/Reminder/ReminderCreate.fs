module ClaimCore.Domain.Tasks.Reminder.Create

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "reminder.create"
        ActSubject.Reminder
        true
        ActFamily.Business
        Consequence.Routine
        [ Capability.ManageCaseWork ]
        (operation :: caseTarget :: ReminderRules.planInputs)

let private capability = List.exactlyOne declaration.Capabilities

type private Command = { Plan: ReminderPlan }

let private parse inputs =
    ReminderRules.plan inputs |> Result.map (fun plan -> { Plan = plan })

let check inputs = Parsing.check parse inputs

let availability environment state binding =
    FamilyRules.availability
        declaration
        environment
        state
        binding
        capability
        (FamilyRules.active true)

let decide context environment (state: DecisionFacts option) binding inputs =
    match state, binding with
    | Some state, Binding.ReminderNew _ ->
        FamilyRules.admit context environment state binding capability (FamilyRules.active true)
        |> Result.bind (fun () -> parse inputs)
        |> Result.bind (fun command ->
            let id = ReminderId.ofOperationId context.Stamp.OperationId

            if Map.containsKey id state.Reminders then
                Error Refusal.IdempotencyConflict
            else
                ReminderRules.admitPlan context command.Plan
                |> Result.map (fun plan ->
                    [
                        ReminderRules.change
                            context
                            state
                            id
                            Revision.first
                            {
                                State = ReminderState.Pending
                                Plan = Some plan
                            }
                    ]))
        |> Parsing.reject
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let decideRetained context environment (state: DecisionFacts option) binding (_: InputMap) =
    match state, binding with
    | Some state, Binding.ReminderNew _ ->
        FamilyRules.admit context environment state binding capability (FamilyRules.active true)
        |> Result.bind (fun () ->
            let id = ReminderId.ofOperationId context.Stamp.OperationId

            if Map.containsKey id state.Reminders then
                Error Refusal.IdempotencyConflict
            else
                Ok
                    [
                        ReminderRules.change
                            context
                            state
                            id
                            Revision.first
                            {
                                State = ReminderState.Pending
                                Plan = None
                            }
                    ])
        |> Parsing.reject
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let task =
    Task.Family
        {
            Declaration = declaration
            Authorize =
                fun environment state _ ->
                    match state with
                    | Some state ->
                        FamilyRules.permission environment (Scope.Case state.Id) capability
                    | None -> Error FamilyRules.unavailable
            Check = check
            Availability = availability
            Decide = decide
            DecideRetained = decideRetained
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.family
        }
