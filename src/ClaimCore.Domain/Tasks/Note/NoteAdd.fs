module ClaimCore.Domain.Tasks.Note.Add

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "note.add"
        ActSubject.Note
        true
        ActFamily.Business
        Consequence.Routine
        [ Capability.ManageCaseWork ]
        [ operation; caseTarget; required (target InputTarget.NoteText) ]

let private capability = List.exactlyOne declaration.Capabilities

type private Command = { Text: NoteText }

let private parse inputs =
    Parsing.scalar InputTarget.NoteText NoteText.parse inputs
    |> Result.map (fun text -> { Text = text })

let check inputs = Parsing.check parse inputs

let availability environment state binding =
    FamilyRules.availability
        declaration
        environment
        state
        binding
        capability
        (FamilyRules.active false)

let decide context environment (state: CaseFamily option) binding inputs =
    match state, binding with
    | Some state, Binding.NoteNew _ ->
        FamilyRules.admit context environment state binding capability (FamilyRules.active false)
        |> Result.bind (fun () -> parse inputs)
        |> Result.bind (fun command ->
            let id = NoteId.ofOperationId context.Stamp.OperationId

            if Map.containsKey id state.Notes then
                Error Refusal.IdempotencyConflict
            else
                let admitted =
                    if context.Decision.Lifetime = DecisionLifetime.Recorded then
                        Ok command.Text
                    else
                        NoteText.admit command.Text

                admitted
                |> Parsing.at InputTarget.NoteText
                |> Result.map (fun text ->
                    [
                        FamilyRules.change
                            context
                            state.Id
                            (NoteId.value id)
                            Revision.first
                            (PartValue.Note { Removed = false; Text = Some text })
                    ]))
        |> Parsing.reject
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let task =
    Task.Family
        {
            Declaration = declaration
            Check = check
            Availability = availability
            Decide = decide
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.family
        }
