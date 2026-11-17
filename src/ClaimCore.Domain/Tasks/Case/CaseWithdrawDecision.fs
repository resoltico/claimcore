module ClaimCore.Domain.Tasks.Case.WithdrawDecision

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "case.withdrawDecision"
        ActSubject.Case
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.Decide ]
        [ operation; caseTarget; revision ]

let private capability = List.exactlyOne declaration.Capabilities

type private Command = { Content: CaseContent }
let check (_: InputMap) = []

let private gate =
    CaseRules.gate true (fun state ->
        state.Case.Value.Content
        |> Option.exists (fun content ->
            content.Finance.Decision.IsSome && content.Finance.PaymentCount = 0L))

let availability environment state binding =
    CaseRules.availability declaration environment state binding capability gate

let decide context environment (state: DecisionFacts option) binding (_: InputMap) =
    match state, binding with
    | Some state, Binding.Case _ ->
        CaseRules.admit context environment state binding capability gate
        |> Parsing.reject
        |> Result.bind (fun () -> CaseRules.content state)
        |> Result.map (fun content ->
            let command =
                {
                    Content =
                        { content with
                            Finance = FinancialPosition.undecided
                        }
                }

            [
                CaseRules.change
                    context
                    state
                    { state.Case.Value with
                        Content = Some command.Content
                    }
            ])
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let decideRetained context environment state binding (_: InputMap) =
    let publicGate state =
        FamilyRules.active true state
        |> Option.orElseWith (fun () ->
            if state.Payments |> Map.exists (fun _ part -> not part.Value.Removed) then
                Some(FamilyRules.notOffered state)
            else
                None)

    CaseRules.retained context environment state binding capability publicGate id

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
            DecisionFactsAvailable = CaseRules.contentAvailable
            Describe = PartDescriptions.family
        }
