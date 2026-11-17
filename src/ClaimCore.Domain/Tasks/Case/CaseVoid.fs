module ClaimCore.Domain.Tasks.Case.Void

open ClaimCore.Domain
open ClaimCore.Domain.ResultFlow
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "case.void"
        ActSubject.Case
        false
        ActFamily.Stewardship
        Consequence.Stewardship
        [ Capability.Steward ]
        [ operation; caseTarget; revision; required (target InputTarget.Reason) ]

let private capability = List.exactlyOne declaration.Capabilities

type private Command = { Reason: Reason }

let private parse inputs =
    Parsing.scalar InputTarget.Reason Reason.parse inputs
    |> Result.map (fun reason -> { Reason = reason })

let check inputs = Parsing.check parse inputs

let private gate (state: DecisionFacts) =
    if state.Case.Value.Lifecycle = Lifecycle.Active then
        None
    else
        Some(FamilyRules.notOffered state)

let availability environment state binding =
    CaseRules.availability declaration environment state binding capability gate

let decide (context: TaskContext) environment (state: DecisionFacts option) binding inputs =
    let parsed = parse inputs

    match state, binding with
    | Some state, Binding.Case _ ->
        result {
            do!
                CaseRules.admitInputs parsed context environment state binding capability
                |> Parsing.reject

            let! command = parsed |> Parsing.reject

            let! reason =
                Reason.admit command.Reason |> Parsing.at InputTarget.Reason |> Parsing.reject

            do! CaseRules.checkState gate state

            return
                [
                    CaseRules.changeWithDisposition
                        context
                        state
                        { state.Case.Value with
                            Lifecycle = Lifecycle.Voided
                        }
                        (Some(CaseDisposition.Voided reason))
                ]
        }
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let decideRetained context environment state binding (_: InputMap) =
    CaseRules.retained context environment state binding capability gate (fun value ->
        { value with
            Lifecycle = Lifecycle.Voided
        })

let task =
    Task.Family
        {
            Declaration = declaration
            Authorize =
                fun environment state _ ->
                    match state with
                    | Some state ->
                        FamilyRules.permission environment (Scope.Case state.Id) capability
                    | None -> Error FamilyRules.unavailable
            Check = check
            Availability = availability
            Decide = decide
            DecideRetained = decideRetained
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.family
        }
