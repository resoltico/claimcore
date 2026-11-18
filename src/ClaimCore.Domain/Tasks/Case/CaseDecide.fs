module ClaimCore.Domain.Tasks.Case.Decide

open ClaimCore.Domain
open ClaimCore.Domain.ResultFlow
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "case.decide"
        ActSubject.Case
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.Decide ]
        (operation
         :: caseTarget
         :: revision
         :: group TaskInputMode.Required RecordGroup.Decision)

let private capability = List.exactlyOne declaration.Capabilities

type private Command = { Decision: Decision }

let private parse admit fields =
    CaseParsing.decision admit fields
    |> Result.map (fun decision -> { Decision = decision })

let check inputs =
    Parsing.check (CaseParsing.values >> parse (fun _ -> Ok)) inputs

let private gate = FamilyRules.active true

let availability environment state binding =
    let offered =
        CaseRules.availability declaration environment state binding capability gate

    let recommendation =
        state
        |> Option.bind (fun state -> state.Case.Value.Content)
        |> Option.bind (fun content -> if content.Finance.Decision.IsNone then Some 0 else None)

    { offered with
        Recommendation = recommendation
    }

let decide (context: TaskContext) environment (state: DecisionFacts option) binding inputs =
    let fields = CaseParsing.values inputs
    let syntax = parse (fun _ -> Ok) fields

    match state, binding, context.Decision.BusinessDate with
    | Some state, Binding.Case _, Some today ->
        result {
            do!
                CaseRules.admitInputs syntax context environment state binding capability
                |> Parsing.reject

            let before = state.Case.Value.Content

            let admit = CaseAdmission.fieldAdmission today before

            let! command = parse admit fields |> Parsing.reject

            let! content = CaseRules.acceptedContent gate state

            let finance = FinancialPosition.withDecision (Some command.Decision) content.Finance

            return
                [
                    CaseRules.change
                        context
                        state
                        { state.Case.Value with
                            Content = Some { content with Finance = finance }
                        }
                ]
        }
        |> Result.map (TaskContext.changes context)
    | _, _, None -> Error(Rejection.InvalidState RecordViolation.InvalidSubjectState)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let decideRetained context environment state binding (_: InputMap) =
    CaseRules.retained context environment state binding capability (FamilyRules.active true) id

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
            DecisionFactsAvailable = CaseRules.contentAvailable
            Describe = PartDescriptions.family
        }
