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

let private current =
    ReminderRules.current true [ ReminderState.Completed; ReminderState.Cancelled ]

let availability environment state binding =
    ReminderRules.availability declaration current environment state binding

let decide context environment (state: DecisionFacts option) binding inputs =
    match state with
    | None -> Error(Rejection.Refused FamilyRules.unavailable)
    | Some state ->
        FamilyRules.admit context environment state binding capability (FamilyRules.active false)
        |> Result.bind (fun () -> current state binding)
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
                            (ReminderValue.Available(
                                { plan with Due = command.Due },
                                ReminderHandling.Pending
                            ))
                    ]))
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
