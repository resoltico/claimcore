module ClaimCore.Domain.Tasks.Reminder.CompleteAndPlanNext

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "reminder.completeAndPlanNext"
        ActSubject.Reminder
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.ManageCaseWork ]
        (operation :: caseTarget :: ReminderRules.identityInputs
         @ ReminderRules.planInputs)

let private capability = List.exactlyOne declaration.Capabilities

type private Command = { Plan: ReminderPlan }

let private parse inputs =
    ReminderRules.plan inputs |> Result.map (fun plan -> { Plan = plan })

let check inputs = Parsing.check parse inputs

let availability environment state binding =
    ReminderRules.availability declaration true [ ReminderState.Pending ] environment state binding

let decide context environment (state: CaseFamily option) binding inputs =
    match state with
    | None -> Error(Rejection.Refused FamilyRules.unavailable)
    | Some state ->
        FamilyRules.admit context environment state binding capability (FamilyRules.active false)
        |> Result.bind (fun () ->
            ReminderRules.current true [ ReminderState.Pending ] state binding)
        |> Result.bind (fun (id, before) ->
            parse inputs
            |> Result.bind (fun command ->
                ReminderRules.admitPlan context command.Plan
                |> Result.bind (fun plan ->
                    let next = ReminderId.ofOperationId context.Stamp.OperationId

                    if Map.containsKey next state.Reminders then
                        Error Refusal.IdempotencyConflict
                    else
                        Ok
                            [
                                ReminderRules.change
                                    context
                                    state
                                    id
                                    (Revision.successor before.Revision)
                                    { before.Value with
                                        State = ReminderState.Completed
                                    }
                                ReminderRules.change
                                    context
                                    state
                                    next
                                    Revision.first
                                    {
                                        State = ReminderState.Pending
                                        Plan = Some plan
                                    }
                            ])))
        |> Parsing.reject
        |> Result.map (TaskContext.changes context)

let task =
    Task.Family
        {
            Declaration = declaration
            Check = check
            Availability = availability
            Decide = decide
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.family
        }
