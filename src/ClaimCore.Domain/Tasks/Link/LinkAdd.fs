module ClaimCore.Domain.Tasks.Link.Add

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "link.add"
        ActSubject.Link
        true
        ActFamily.Business
        Consequence.Routine
        [ Capability.ManageCaseWork ]
        [
            operation
            caseTarget
            optional (target InputTarget.LinkTargetReference)
            optional (target InputTarget.LinkTargetId)
            optional (target InputTarget.LinkNote)
        ]

let private capability = List.exactlyOne declaration.Capabilities

let private note inputs =
    Parsing.optionalText InputTarget.LinkNote inputs
    |> Result.bind (function
        | None -> Ok None
        | Some text -> NoteText.parse text |> Parsing.at InputTarget.LinkNote |> Result.map Some)

let check inputs =
    Parsing.check
        (fun inputs ->
            Admission.map2
                (fun target note -> target, note)
                (RelatedCaseInput.selector inputs)
                (note inputs))
        inputs

let private current (state: DecisionFacts) =
    state.Links
    |> Map.values
    |> Seq.filter (fun link -> not link.Value.Removed && link.Value.Content.IsSome)
    |> Seq.toList

let private gate state =
    FamilyRules.active false state
    |> Option.orElseWith (fun () ->
        if (current state).Length >= RelatedCase.MaximumCurrent then
            Some(Refusal.Case CaseRefusal.LinkLimitReached)
        else
            None)

let availability environment state binding =
    FamilyRules.availability declaration environment state binding capability gate

let private effect (context: TaskContext) (state: DecisionFacts) content =
    let id = LinkId.ofOperationId context.Stamp.OperationId

    [
        FamilyRules.change
            context
            state.Id
            (LinkId.value id)
            Revision.first
            (PartValue.Link { Removed = false; Content = content })
    ]

let decide context (environment: FamilyEnvironment) (state: DecisionFacts option) binding inputs =
    match state, binding with
    | Some state, Binding.LinkNew _ ->
        FamilyRules.admit context environment state binding capability gate
        |> Result.bind (fun () -> RelatedCaseInput.selector inputs)
        |> Result.bind (fun target ->
            match environment.RelatedCase target |> RelatedCase.availableTarget with
            | Some id ->
                if id = state.Id then
                    Error(Refusal.Case CaseRefusal.SelfLink)
                elif
                    current state
                    |> List.exists (fun link ->
                        link.Value.Content |> Option.exists (fun content -> content.Target = id))
                then
                    Error(Refusal.Case CaseRefusal.DuplicateLink)
                else
                    Ok id
            | None -> Error FamilyRules.unavailable)
        |> Result.bind (fun target ->
            note inputs
            |> Result.bind (fun note ->
                if context.Decision.Lifetime = DecisionLifetime.Recorded then
                    Ok note
                else
                    match note with
                    | None -> Ok None
                    | Some text ->
                        NoteText.admit text |> Parsing.at InputTarget.LinkNote |> Result.map Some)
            |> Result.map (fun note ->
                effect context state (Some { Target = target; Note = note })))
        |> Parsing.reject
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let decideRetained context environment (state: DecisionFacts option) binding (_: InputMap) =
    match state, binding with
    | Some state, Binding.LinkNew _ ->
        FamilyRules.admit context environment state binding capability gate
        |> Result.map (fun () -> effect context state None)
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
            DecideRetained = decideRetained
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.family
        }
