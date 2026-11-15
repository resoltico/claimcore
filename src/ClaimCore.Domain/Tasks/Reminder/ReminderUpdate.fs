module ClaimCore.Domain.Tasks.Reminder.Update

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "reminder.update"
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
                if before.Value.Plan = Some command.Plan then
                    Error(Refusal.Reminder ReminderRuleRefusal.NoChanges)
                else
                    let admitted =
                        if
                            before.Value.Plan
                            |> Option.exists (fun plan -> plan.Action = command.Plan.Action)
                        then
                            Ok command.Plan
                        else
                            ReminderRules.admitPlan context command.Plan

                    admitted
                    |> Result.map (fun plan ->
                        [
                            ReminderRules.change
                                context
                                state
                                id
                                (Revision.successor before.Revision)
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
            DecisionFactsAvailable = ReminderRules.planAvailable
            Describe = PartDescriptions.family
        }
