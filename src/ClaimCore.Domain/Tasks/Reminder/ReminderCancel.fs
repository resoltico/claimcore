module ClaimCore.Domain.Tasks.Reminder.Cancel

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "reminder.cancel"
        ActSubject.Reminder
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.ManageCaseWork ]
        (operation :: caseTarget :: ReminderRules.identityInputs)

let private capability = List.exactlyOne declaration.Capabilities

type private Command =
    {
        ReminderId: ReminderId
        Before: AcceptedPart<ReminderValue>
    }

let check (_: InputMap) = []

let private current = ReminderRules.current false [ ReminderState.Pending ]

let availability environment state binding =
    ReminderRules.availability declaration current environment state binding

let decide context environment (state: DecisionFacts option) binding (_: InputMap) =
    match state with
    | None -> Error(Rejection.Refused FamilyRules.unavailable)
    | Some state ->
        FamilyRules.admit context environment state binding capability (FamilyRules.active false)
        |> Result.bind (fun () -> current state binding)
        |> Result.map (fun (id, before) ->
            let command = { ReminderId = id; Before = before }

            [
                ReminderRules.change
                    context
                    state
                    command.ReminderId
                    (Revision.successor command.Before.Revision)
                    { command.Before.Value with
                        State = ReminderState.Cancelled
                    }
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
            DecideRetained = decide
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.family
        }
