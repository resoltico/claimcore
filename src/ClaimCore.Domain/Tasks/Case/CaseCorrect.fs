module ClaimCore.Domain.Tasks.Case.Correct

open ClaimCore.Domain
open ClaimCore.Domain.ResultFlow
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "case.correct"
        ActSubject.Case
        false
        ActFamily.Business
        Consequence.Routine
        [
            Access.correctionCapability RecordGroup.Registration
            Access.correctionCapability RecordGroup.Decision
        ]
        (operation
         :: caseTarget
         :: revision
         :: [
             required (target (InputTarget.Group RecordGroup.Registration))
             required (target (InputTarget.Group RecordGroup.Decision))
         ]
         @ group TaskInputMode.Update RecordGroup.Registration
         @ group TaskInputMode.Update RecordGroup.Decision)

[<RequireQualifiedAccess>]
type private RegistrationChoice =
    | Keep
    | Update of ValidRegistrationUpdate

[<RequireQualifiedAccess>]
type private DecisionChoice =
    | Keep
    | Update of ValidDecisionUpdate
    | Clear

type private Command =
    {
        Registration: RegistrationChoice
        Decision: DecisionChoice
    }

let private choice group inputs =
    Parsing.text (InputTarget.Group group) inputs

let private hasFields group (inputs: InputMap) =
    BusinessField.ofGroup group
    |> List.exists (fun field -> Map.containsKey (Parsing.field field) inputs.Values)

let private invalid group =
    Error(Refusal.invalidInput (InputTarget.Group group, Violation.UpdateRequired))

let private parse admit inputs fields =
    let registration =
        choice RecordGroup.Registration inputs
        |> Result.bind (function
            | "KEEP" when not (hasFields RecordGroup.Registration inputs) ->
                Ok RegistrationChoice.Keep
            | "UPDATE" ->
                CaseParsing.registrationUpdate admit fields
                |> Result.map RegistrationChoice.Update
            | _ -> invalid RecordGroup.Registration)

    let decision =
        choice RecordGroup.Decision inputs
        |> Result.bind (function
            | "KEEP" when not (hasFields RecordGroup.Decision inputs) -> Ok DecisionChoice.Keep
            | "CLEAR" when not (hasFields RecordGroup.Decision inputs) -> Ok DecisionChoice.Clear
            | "UPDATE" ->
                CaseParsing.decisionUpdate admit fields |> Result.map DecisionChoice.Update
            | _ -> invalid RecordGroup.Decision)

    Admission.map2
        (fun registration decision ->
            {
                Registration = registration
                Decision = decision
            })
        registration
        decision

let check inputs =
    Parsing.check (fun inputs -> parse (fun _ -> Ok) inputs (CaseParsing.values inputs)) inputs

let private gate =
    CaseRules.gate false (fun state ->
        state.Case.Value.Status = CaseStatus.Closed
        || (state.Case.Value.Content
            |> Option.exists (fun content -> content.Finance.Decision.IsSome)))

let private canFor principal access id capability =
    FamilyRules.permissionFor principal access (Scope.Case id) capability
    |> Result.isOk

let private anyPermissionFor principal access id =
    if
        canFor principal access id Capability.Register
        || canFor principal access id Capability.Decide
    then
        Ok()
    else
        FamilyRules.permissionFor principal access (Scope.Case id) Capability.Register

let private permissionsFor principal access id inputs =
    anyPermissionFor principal access id
    |> Result.bind (fun () ->
        [
            if choice RecordGroup.Registration inputs = Ok "UPDATE" then
                yield
                    FamilyRules.permissionFor principal access (Scope.Case id) Capability.Register
            if
                choice RecordGroup.Decision inputs
                |> Result.toOption
                |> Option.exists (fun value -> value = "UPDATE" || value = "CLEAR")
            then
                yield FamilyRules.permissionFor principal access (Scope.Case id) Capability.Decide
        ]
        |> Admission.collect)

let private can (environment: FamilyEnvironment) (state: DecisionFacts) capability =
    canFor environment.RequesterPrincipal environment.Requester state.Id capability

let private anyPermission (environment: FamilyEnvironment) (state: DecisionFacts) =
    anyPermissionFor environment.RequesterPrincipal environment.Requester state.Id

let private permissions (environment: FamilyEnvironment) (state: DecisionFacts) inputs =
    permissionsFor environment.RequesterPrincipal environment.Requester state.Id inputs

let decisionChoices (finance: FinancialPosition) =
    if finance.Decision.IsNone then
        [ CorrectionGroupAction.Keep ]
    else
        [
            CorrectionGroupAction.Keep
            CorrectionGroupAction.Update
            CorrectionGroupAction.Clear
        ]

let availability environment state binding =
    let offered =
        CaseRules.availability
            declaration
            environment
            state
            binding
            Capability.ReadCase
            (fun state ->
                match anyPermission environment state with
                | Error refusal -> Some refusal
                | Ok() -> gate state)

    match state with
    | None -> offered
    | Some state ->
        let canRegistration = can environment state Capability.Register
        let canDecision = can environment state Capability.Decide

        let editable =
            offered.Editable
            |> List.filter (function
                | TaskInputId.Target(InputTarget.Group RecordGroup.Registration) -> canRegistration
                | TaskInputId.Target(InputTarget.Group RecordGroup.Decision) -> canDecision
                | TaskInputId.Target(InputTarget.Field field) ->
                    match (BusinessField.describe field).Group with
                    | Some RecordGroup.Registration -> canRegistration
                    | Some RecordGroup.Decision -> canDecision
                    | _ -> true
                | _ -> true)

        let content = state.Case.Value.Content

        let groupSeeds =
            Map.ofList
                [
                    target (InputTarget.Group RecordGroup.Registration), InputValue.Text "KEEP"
                    target (InputTarget.Group RecordGroup.Decision), InputValue.Text "KEEP"
                ]

        let seeds =
            content
            |> Option.map (fun content ->
                CaseRules.seeds (
                    CaseFields.groupValues content.Registration content.Finance.Decision
                ))
            |> Option.defaultValue Map.empty
            |> Map.fold (fun seeds key value -> Map.add key value seeds) groupSeeds

        let token = Vocabulary.token CorrectionGroupAction.vocabulary

        let decision =
            content
            |> Option.map (fun content -> decisionChoices content.Finance)
            |> Option.defaultValue [ CorrectionGroupAction.Keep ]

        { offered with
            Recommendation =
                if state.Case.Value.Status = CaseStatus.Opened then
                    Some 2
                else
                    None
            Editable = editable
            Seeds = seeds
            Choices =
                Map.ofList
                    [
                        target (InputTarget.Group RecordGroup.Registration),
                        (if canRegistration then [ "KEEP"; "UPDATE" ] else [ "KEEP" ])
                        target (InputTarget.Group RecordGroup.Decision),
                        (if canDecision then List.map token decision else [ "KEEP" ])
                    ]
        }

let private apply (before: CaseContent) command =
    let registration =
        match command.Registration with
        | RegistrationChoice.Keep -> before.Registration
        | RegistrationChoice.Update update -> RegistrationUpdate.apply update before.Registration

    let decision =
        match command.Decision, before.Finance.Decision with
        | DecisionChoice.Keep, current -> Ok current
        | _, None -> Error(Refusal.Case CaseRefusal.CorrectionRequiresExistingValue)
        | DecisionChoice.Clear, Some _ -> Ok None
        | DecisionChoice.Update update, Some current ->
            Ok(Some(DecisionUpdate.apply update current))

    decision
    |> Parsing.reject
    |> Result.bind (fun decision ->
        let after =
            {
                Registration = registration
                Finance = FinancialPosition.withDecision decision before.Finance
            }

        if after = before then
            Error(Rejection.Refused(Refusal.Case CaseRefusal.CorrectionNoChanges))
        else
            Ok after)

let decide (context: TaskContext) environment (state: DecisionFacts option) binding inputs =
    let fields = CaseParsing.values inputs
    let syntax = parse (fun _ -> Ok) inputs fields

    match state, binding, context.Decision.BusinessDate with
    | Some state, Binding.Case _, Some today ->
        result {
            do!
                CaseRules.admitInputsWithPermission
                    syntax
                    context
                    environment
                    state
                    binding
                    (permissions environment state inputs)
                |> Parsing.reject

            let before = state.Case.Value.Content

            let admit = CaseAdmission.fieldAdmission today before

            let! command = parse admit inputs fields |> Parsing.reject

            let! content = CaseRules.acceptedContent gate state
            let! after = apply content command

            return
                [
                    CaseRules.change
                        context
                        state
                        { state.Case.Value with
                            Content = Some after
                        }
                ]
        }
        |> Result.map (TaskContext.changes context)
    | _, _, None -> Error(Rejection.InvalidState RecordViolation.InvalidSubjectState)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let decideRetained context environment (state: DecisionFacts option) binding (_: InputMap) =
    match state, binding with
    | Some state, Binding.Case _ ->
        FamilyRules.admitPermission
            context
            environment
            state
            binding
            (anyPermission environment state)
            (fun state -> CaseRules.reviewed binding state (FamilyRules.active false))
        |> Parsing.reject
        |> Result.map (fun () ->
            [ CaseRules.change context state { state.Case.Value with Content = None } ])
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let task =
    Task.Family
        {
            Declaration = declaration
            Authorize =
                fun principal access caseId inputs ->
                    match caseId with
                    | Some id -> permissionsFor principal access id inputs
                    | None -> Error FamilyRules.unavailable
            Check = check
            Availability = availability
            Decide = decide
            DecideRetained = decideRetained
            DecisionFactsAvailable = CaseRules.contentAvailable
            Describe = PartDescriptions.family
        }
