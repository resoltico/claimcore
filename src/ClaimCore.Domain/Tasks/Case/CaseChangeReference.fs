module ClaimCore.Domain.Tasks.Case.ChangeReference

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "case.changeReference"
        ActSubject.Case
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.Register ]
        [
            operation
            caseTarget
            revision
            required (target InputTarget.NewCaseReference) |> retained
        ]

let private capability = List.exactlyOne declaration.Capabilities

let private eligible policy (state: DecisionFacts) =
    CaseReferencePolicy.allowsCustom policy
    && not (CaseReference.isGenerated state.Reference)

let private gate policy = CaseRules.gate false (eligible policy)

let private parse =
    Parsing.scalar InputTarget.NewCaseReference CaseReference.parseCustom

let check inputs = Parsing.check parse inputs

let availability (environment: FamilyEnvironment) state binding =
    CaseRules.availability
        declaration
        environment
        state
        binding
        capability
        (gate environment.CaseReferencePolicy)

let decide context (environment: FamilyEnvironment) (state: DecisionFacts option) binding inputs =
    match state, binding with
    | Some state, Binding.Case _ ->
        let syntax = parse inputs

        CaseRules.admitInputs syntax context environment state binding capability
        |> Result.bind (fun () -> syntax)
        |> Result.bind (fun reference ->
            match gate environment.CaseReferencePolicy state with
            | Some refusal -> Error refusal
            | None when state.Reference = reference ->
                Error(Refusal.Case CaseRefusal.CorrectionNoChanges)
            | None ->
                let owner = environment.ReferenceOwner reference

                if owner |> Option.exists ((<>) state.Id) then
                    Error(Refusal.Case CaseRefusal.ReferenceUnavailable)
                else
                    let change = CaseRules.change context state state.Case.Value

                    match change.Value with
                    | PartValue.Case value ->
                        Ok
                            [
                                { change with
                                    NaturalKey =
                                        if owner.IsSome then
                                            None
                                        else
                                            Some(CaseReference.format reference)
                                    Value =
                                        PartValue.Case
                                            { value with
                                                Reference = Some reference
                                            }
                                }
                            ]
                    | _ -> invalidOp "Reference changes require a case effect.")
        |> Parsing.reject
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

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
