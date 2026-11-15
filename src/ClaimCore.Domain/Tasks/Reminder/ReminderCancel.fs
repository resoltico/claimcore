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

let availability environment state binding =
    ReminderRules.availability declaration false [ ReminderState.Pending ] environment state binding

let decide context environment (state: CaseFamily option) binding (_: InputMap) =
    match state with
    | None -> Error(Rejection.Refused FamilyRules.unavailable)
    | Some state ->
        FamilyRules.admit context environment state binding capability (FamilyRules.active false)
        |> Result.bind (fun () ->
            ReminderRules.current false [ ReminderState.Pending ] state binding)
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
            Check = check
            Availability = availability
            Decide = decide
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.family
        }
