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

let private parse defaultKind inputs =
    ReminderRules.plan defaultKind inputs
    |> Result.map (fun plan -> { Plan = plan })

let check inputs =
    Parsing.check (parse ReminderKind.defaultValue) inputs

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
            let currentKind =
                before.Value.Plan
                |> Option.map _.Kind
                |> Option.defaultValue ReminderKind.defaultValue

            parse currentKind inputs
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
                                (ReminderValue.Available(plan, ReminderHandling.Pending))
                        ])))
        |> Parsing.reject
        |> Result.map (TaskContext.changes context)

let decideRetained context environment state binding (_: InputMap) =
    ReminderRules.retained context environment state binding current ReminderState.Pending

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
            DecisionFactsAvailable = ReminderRules.planAvailable
            Describe = PartDescriptions.family
        }
