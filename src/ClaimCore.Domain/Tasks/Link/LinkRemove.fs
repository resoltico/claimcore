module ClaimCore.Domain.Tasks.Link.Remove

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "link.remove"
        ActSubject.Link
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.ManageCaseWork ]
        [ operation; caseTarget; required (target InputTarget.LinkId) |> bound ]

let private capability = List.exactlyOne declaration.Capabilities
let check (_: InputMap) = []

let private selected binding (state: DecisionFacts) =
    match binding with
    | Binding.Link(_, id) ->
        Map.tryFind id state.Links
        |> Option.filter (fun part -> not part.Value.Removed)
        |> Option.map (fun part -> id, part)
    | _ -> None

let private gate retained (environment: FamilyEnvironment) binding state =
    FamilyRules.active false state
    |> Option.orElseWith (fun () ->
        let aloud =
            selected binding state
            |> Option.exists (fun (_, part) ->
                match part.Value.Content with
                | Some content ->
                    environment.RelatedCase(CaseSelector.Id content.Target) |> Option.isSome
                | None -> retained)

        if aloud then None else Some FamilyRules.unavailable)

let availability environment state binding =
    FamilyRules.availability
        declaration
        environment
        state
        binding
        capability
        (gate false environment binding)

let private apply retained context environment (state: DecisionFacts option) binding (_: InputMap) =
    match state with
    | Some state ->
        FamilyRules.admit
            context
            environment
            state
            binding
            capability
            (gate retained environment binding)
        |> Result.bind (fun () ->
            match selected binding state with
            | None -> Error FamilyRules.unavailable
            | Some(id, part) ->
                Ok
                    [
                        FamilyRules.change
                            context
                            state.Id
                            (LinkId.value id)
                            (Revision.successor part.Revision)
                            (PartValue.Link { Removed = true; Content = None })
                    ])
        |> Result.mapError Rejection.ofRefusal
        |> Result.map (TaskContext.changes context)
    | None -> Error(Rejection.Refused FamilyRules.unavailable)

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
            Decide = apply false
            DecideRetained = apply true
            DecisionFactsAvailable =
                fun state binding ->
                    state
                    |> Option.bind (selected binding)
                    |> Option.forall (fun (_, part) -> part.Value.Content.IsSome)
            Describe = PartDescriptions.family
        }
