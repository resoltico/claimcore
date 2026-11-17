module ClaimCore.Domain.Tasks.Note.Remove

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "note.remove"
        ActSubject.Note
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.ManageCaseWork ]
        [ operation; caseTarget; required (target InputTarget.NoteId) |> bound ]

let private capability = List.exactlyOne declaration.Capabilities

type private Command = { NoteId: NoteId }
let check (_: InputMap) = []

let private gate binding state =
    FamilyRules.active false state
    |> Option.orElseWith (fun () ->
        match binding with
        | Binding.Note(_, id) when
            state.Notes
            |> Map.tryFind id
            |> Option.exists (fun part -> not part.Value.Removed)
            ->
            None
        | _ -> Some FamilyRules.unavailable)

let availability environment state binding =
    FamilyRules.availability declaration environment state binding capability (gate binding)

let decide context environment (state: DecisionFacts option) binding (_: InputMap) =
    match state, binding with
    | Some state, Binding.Note(_, id) ->
        let command = { NoteId = id }

        FamilyRules.admit context environment state binding capability (gate binding)
        |> Result.bind (fun () ->
            let before = Map.find command.NoteId state.Notes
            FamilyRules.revision before.Revision before.Revision)
        |> Result.map (fun () ->
            let before = Map.find command.NoteId state.Notes

            [
                FamilyRules.change
                    context
                    state.Id
                    (NoteId.value command.NoteId)
                    (Revision.successor before.Revision)
                    (PartValue.Note { Removed = true; Text = None })
            ])
        |> Result.mapError Rejection.ofRefusal
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
