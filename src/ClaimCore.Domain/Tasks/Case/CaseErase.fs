module ClaimCore.Domain.Tasks.Case.Erase

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    { family
          "case.erase"
          ActSubject.Case
          false
          ActFamily.Stewardship
          Consequence.Irreversible
          [ Capability.Steward ]
          [
              operation
              caseTarget
              revision
              required (target InputTarget.ExpectedFamilySequence)
          ] with
        ReviewsFamilySequence = true
    }

let private capability = List.exactlyOne declaration.Capabilities

type private Command = { FamilySequence: Sequence }
let check (_: InputMap) = []

let private gate (state: CaseFamily) =
    if state.Case.Value.Lifecycle = Lifecycle.Erased then
        Some(FamilyRules.notOffered state)
    else
        None

let availability environment state binding =
    CaseRules.availability declaration environment state binding capability gate

let decide context environment (state: CaseFamily option) binding (_: InputMap) =
    match state, binding with
    | Some state, Binding.CaseErase(_, _, sequence) ->
        let command = { FamilySequence = sequence }

        CaseRules.admit context environment state binding capability gate
        |> Result.bind (fun () ->
            if command.FamilySequence <> state.Sequence then
                Error(Refusal.Case CaseRefusal.FamilyChanged)
            else
                let value =
                    { state.Case.Value with
                        Lifecycle = Lifecycle.Erased
                        Content = None
                    }

                Ok
                    [
                        { CaseRules.change context state value with
                            Erases = Some(CompartmentOwner.Family state.Id)
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
