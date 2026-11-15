module ClaimCore.Domain.Tasks.Case.Reinstate

open ClaimCore.Domain
open ClaimCore.Domain.ResultFlow
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "case.reinstate"
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

let private gate (state: CaseFamily) =
    if state.Case.Value.Lifecycle = Lifecycle.Voided then
        None
    else
        Some(FamilyRules.notOffered state)

let availability environment state binding =
    CaseRules.availability declaration environment state binding capability gate

let decide (context: TaskContext) environment (state: CaseFamily option) binding inputs =
    let parsed = parse inputs

    match state, binding with
    | Some state, Binding.Case _ ->
        result {
            do!
                CaseRules.admitInputs parsed context environment state binding capability
                |> Parsing.reject

            let! command = parsed |> Parsing.reject

            let! reason =
                (if context.Decision.Lifetime = DecisionLifetime.Recorded then
                     Ok command.Reason
                 else
                     Reason.admit command.Reason)
                |> Parsing.at InputTarget.Reason
                |> Parsing.reject

            do! CaseRules.checkState gate state

            return
                [
                    CaseRules.changeWithDisposition
                        context
                        state
                        { state.Case.Value with
                            Lifecycle = Lifecycle.Active
                        }
                        (Some(CaseDisposition.Reinstated reason))
                ]
        }
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
