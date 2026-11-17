module ClaimCore.Domain.Tasks.Case.AmendRegistration

open ClaimCore.Domain
open ClaimCore.Domain.ResultFlow
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "case.amendRegistration"
        ActSubject.Case
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.Register ]
        (operation
         :: caseTarget
         :: revision
         :: group TaskInputMode.Update RecordGroup.Registration)

let private capability = List.exactlyOne declaration.Capabilities

type private Command = { Update: ValidRegistrationUpdate }

let private parse admit fields =
    CaseParsing.registrationUpdate admit fields
    |> Result.map (fun update -> { Update = update })

let check inputs =
    Parsing.check (CaseParsing.values >> parse (fun _ -> Ok)) inputs

let private gate =
    CaseRules.gate true (fun state ->
        state.Case.Value.Content
        |> Option.exists (fun content -> content.Finance.Decision.IsNone))

let availability environment state binding =
    let offered =
        CaseRules.availability declaration environment state binding capability gate

    let seeds =
        state
        |> Option.bind (fun state -> state.Case.Value.Content)
        |> Option.map (fun content -> CaseRules.seeds (Registration.values content.Registration))
        |> Option.defaultValue Map.empty

    { offered with
        Seeds = seeds
        Recommendation = Some 1
    }

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

            let admit = CaseAdmission.fieldAdmission today before

            let! command =
                Admission.map2
                    (fun command () -> command)
                    (parse admit fields)
                    (CaseParsing.chronology before false false fields)
                |> CaseInput.ordered
                |> Parsing.reject

            let! content = CaseRules.acceptedContent gate state

            let! registration =
                RegistrationUpdate.apply command.Update content.Registration
                |> CaseInput.registrationMerge
                |> Parsing.reject

            return
                [
                    CaseRules.change
                        context
                        state
                        { state.Case.Value with
                            Content =
                                Some
                                    { content with
                                        Registration = registration
                                    }
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
