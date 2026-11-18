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
         @ ReminderRules.planInputs
         @ [ ReminderRules.doneOnInput ])

let private capability = List.exactlyOne declaration.Capabilities

let private parse inputs =
    Admission.map2
        (fun plan doneOn -> plan, doneOn)
        (ReminderRules.plan ReminderKind.defaultValue inputs)
        (ReminderRules.doneOn inputs)

let check inputs = Parsing.check parse inputs

let private current = ReminderRules.current true [ ReminderState.Pending ]

let availability environment state binding =
    ReminderRules.availability declaration current environment state binding

let decide context environment (state: DecisionFacts option) binding inputs =
    match state with
    | None -> Error(Rejection.Refused FamilyRules.unavailable)
    | Some state ->
        FamilyRules.admit context environment state binding capability (FamilyRules.active false)
        |> Result.bind (fun () -> current state binding)
        |> Result.bind (fun (id, before) ->
            parse inputs
            |> Result.bind (fun (plan, doneOn) ->
                Admission.map2
                    (fun plan doneOn -> plan, doneOn)
                    (ReminderRules.admitPlan context plan)
                    (ReminderRules.admitDoneOn context doneOn)
                |> Result.bind (fun (plan, doneOn) ->
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
                                    (before.Value.Plan
                                     |> Option.map (fun previous ->
                                         ReminderValue.Available(
                                             previous,
                                             ReminderHandling.Completed doneOn
                                         ))
                                     |> Option.defaultValue (
                                         ReminderValue.Unavailable ReminderState.Completed
                                     ))
                                ReminderRules.change
                                    context
                                    state
                                    next
                                    Revision.first
                                    (ReminderValue.Available(plan, ReminderHandling.Pending))
                            ])))
        |> Parsing.reject
        |> Result.map (TaskContext.changes context)

let decideRetained context environment (state: DecisionFacts option) binding (_: InputMap) =
    match state with
    | None -> Error(Rejection.Refused FamilyRules.unavailable)
    | Some state ->
        FamilyRules.admit context environment state binding capability (FamilyRules.active false)
        |> Result.bind (fun () -> current state binding)
        |> Result.bind (fun (id, before) ->
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
                            (ReminderValue.Unavailable ReminderState.Completed)
                        ReminderRules.change
                            context
                            state
                            next
                            Revision.first
                            (ReminderValue.Unavailable ReminderState.Pending)
                    ])
        |> Parsing.reject
        |> Result.map (TaskContext.changes context)

let task =
    Task.Family
        {
            Declaration = declaration
            Authorize =
                fun principal access caseId _ ->
                    match caseId with
                    | Some id ->
                        FamilyRules.permissionFor principal access (Scope.Case id) capability
                    | None -> Error FamilyRules.unavailable
            Check = check
            Availability = availability
            Decide = decide
            DecideRetained = decideRetained
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.family
        }
