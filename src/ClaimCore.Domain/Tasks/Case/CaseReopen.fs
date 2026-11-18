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
        [ operation; caseTarget; revision; required (target InputTarget.Reason) ]

let private capability = List.exactlyOne declaration.Capabilities

let private parse admit inputs =
    Parsing.scalar InputTarget.Reason (Reason.parse >> Result.bind admit) inputs

let check inputs = Parsing.check (parse Ok) inputs

let private gate =
    CaseRules.gate false (fun state -> state.Case.Value.Status = CaseStatus.Closed)

let availability environment state binding =
    CaseRules.availability declaration environment state binding capability gate

let private accept context environment (state: DecisionFacts option) binding reason =
    match state, binding with
    | Some state, Binding.Case _ ->
        CaseRules.admit context environment state binding capability gate
        |> Result.bind (fun () -> reason)
        |> Result.map (fun reason ->
            [
                CaseRules.reopen
                    context
                    state
                    { state.Case.Value with
                        Handling = Handling.Open
                    }
                    reason
            ])
        |> Parsing.reject
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let decide context environment state binding inputs =
    accept context environment state binding (parse Reason.admit inputs |> Result.map Some)

let decideRetained context environment state binding (_: InputMap) =
    accept context environment state binding (Ok None)

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
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.family
        }
