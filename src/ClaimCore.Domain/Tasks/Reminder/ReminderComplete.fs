module ClaimCore.Domain.Tasks.Reminder.Complete

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    familyWithReview
        "reminder.complete"
        ActSubject.Reminder
        false
        ActFamily.Business
        Consequence.Routine
        TaskReview.Implicit
        [ Capability.ManageCaseWork ]
        (operation :: caseTarget :: ReminderRules.identityInputs
         @ [ ReminderRules.doneOnInput ])

let private capability = List.exactlyOne declaration.Capabilities

let check inputs =
    Parsing.check ReminderRules.doneOn inputs

let private current = ReminderRules.current false [ ReminderState.Pending ]

let availability environment state binding =
    ReminderRules.availability declaration current environment state binding

let decide context environment (state: DecisionFacts option) binding inputs =
    match state with
    | None -> Error(Rejection.Refused FamilyRules.unavailable)
    | Some state ->
        FamilyRules.admit context environment state binding capability (FamilyRules.active false)
        |> Result.bind (fun () -> current state binding)
        |> Result.bind (fun (id, before) ->
            ReminderRules.doneOn inputs
            |> Result.bind (ReminderRules.admitDoneOn context)
            |> Result.map (fun doneOn ->
                [
                    ReminderRules.change
                        context
                        state
                        id
                        (Revision.successor before.Revision)
                        (before.Value.Plan
                         |> Option.map (fun plan ->
                             ReminderValue.Available(plan, ReminderHandling.Completed doneOn))
                         |> Option.defaultValue (
                             ReminderValue.Unavailable ReminderState.Completed
                         ))
                ]))
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
            DecideRetained =
                fun context environment state binding _ ->
                    ReminderRules.retained
                        context
                        environment
                        state
                        binding
                        current
                        ReminderState.Completed
            DecisionFactsAvailable = ReminderRules.planAvailable
            Describe = PartDescriptions.family
        }
