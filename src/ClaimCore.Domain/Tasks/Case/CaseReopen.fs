module ClaimCore.Domain.Tasks.Case.Reopen

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "case.reopen"
        ActSubject.Case
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.Close ]
        [ operation; caseTarget; revision ]

let private capability = List.exactlyOne declaration.Capabilities

type private Command = { Status: CaseStatus }
let check (_: InputMap) = []

let private gate =
    CaseRules.gate false (fun state -> state.Case.Value.Status = CaseStatus.Closed)

let availability environment state binding =
    CaseRules.availability declaration environment state binding capability gate

let decide context environment (state: CaseFamily option) binding (_: InputMap) =
    match state, binding with
    | Some state, Binding.Case _ ->
        CaseRules.admit context environment state binding capability gate
        |> Result.map (fun () ->
            let command = { Status = CaseStatus.Opened }

            [
                CaseRules.change
                    context
                    state
                    { state.Case.Value with
                        Status = command.Status
                    }
            ])
        |> Parsing.reject
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

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
