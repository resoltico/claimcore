module ClaimCore.Domain.Tasks.Reminder.Reopen

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "reminder.reopen"
        ActSubject.Reminder
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.ManageCaseWork ]
        (operation :: caseTarget :: ReminderRules.identityInputs
         @ [ required (target InputTarget.ReminderDueDate) ])

let private capability = List.exactlyOne declaration.Capabilities

type private Command = { Due: CalendarDate }

let private parse inputs =
    Parsing.scalar InputTarget.ReminderDueDate CalendarDate.parse inputs
    |> Result.map (fun due -> { Due = due })

let check inputs = Parsing.check parse inputs

let availability environment state binding =
    ReminderRules.availability
        declaration
        true
        [ ReminderState.Completed; ReminderState.Cancelled ]
        environment
        state
        binding

let decide context environment (state: CaseFamily option) binding inputs =
    match state with
    | None -> Error(Rejection.Refused FamilyRules.unavailable)
    | Some state ->
        FamilyRules.admit context environment state binding capability (FamilyRules.active false)
        |> Result.bind (fun () ->
            ReminderRules.current
                true
                [ ReminderState.Completed; ReminderState.Cancelled ]
                state
                binding)
        |> Parsing.reject
        |> Result.bind (fun (id, before) ->
            match before.Value.Plan with
            | None -> Error(Rejection.InvalidState RecordViolation.InvalidSubjectState)
            | Some plan ->
                parse inputs
                |> Parsing.reject
                |> Result.map (fun command ->
                    [
                        ReminderRules.change
                            context
                            state
                            id
                            (Revision.successor before.Revision)
                            {
                                State = ReminderState.Pending
                                Plan = Some { plan with Due = command.Due }
                            }
                    ]))
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
