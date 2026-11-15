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
    CaseRules.availability declaration environment state binding capability gate

let decide (context: TaskContext) environment (state: CaseFamily option) binding inputs =
    let fields = CaseParsing.values inputs
    let syntax = parse (fun _ -> Ok) fields

    match state, binding, context.Decision.BusinessDate with
    | Some state, Binding.Case _, Some today ->
        result {
            do!
                CaseRules.admitInputs syntax context environment state binding capability
                |> Parsing.reject

            let before = state.Case.Value.Content

            let admit =
                if context.Decision.Lifetime = DecisionLifetime.Recorded then
                    fun _ -> Ok
                else
                    CaseAdmission.fieldAdmission today before

            let! command =
                Admission.map2
                    (fun command () -> command)
                    (parse admit fields)
                    (CaseParsing.chronology before false false fields)
                |> CaseInput.ordered
                |> Parsing.reject

            let! content = CaseRules.acceptedContent gate state

            do!
                Admission.collect
                    [
                        CaseAdmission.decisionChange content.Finance (Some command.Decision)
                        CaseAdmission.crossFinancial
                            (Decision.values command.Decision |> List.map fst |> Set.ofList)
                            (Registration.incidentNotificationDate content.Registration)
                            (Some command.Decision)
                            content.Finance
                    ]
                |> CaseInput.ordered
                |> Parsing.reject

            let! finance =
                FinancialPosition.withDecision (Some command.Decision) content.Finance
                |> Result.mapError (fun _ ->
                    Rejection.InvalidState RecordViolation.InvalidFinancialPosition)

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

let task =
    Task.Family
        {
            Declaration = declaration
            Check = check
            Availability = availability
            Decide = decide
            DecisionFactsAvailable = CaseRules.contentAvailable
            Describe = PartDescriptions.family
        }
