module ClaimCore.Tests.Tasks.NoteRemoveTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Add = ClaimCore.Domain.Tasks.Note.Add
module Remove = ClaimCore.Domain.Tasks.Note.Remove

[<Tests>]
let tests =
    testList
        "task note.remove"
        [
            testCase "removal reads identity and removal state without loading note text" (fun () ->
                let state = family ()

                let state =
                    Add.decide
                        (context 2)
                        environment
                        (Some state)
                        (Binding.NoteNew(target state))
                        (inputs [ text InputTarget.NoteText "private note" ])
                    |> accepted
                    |> apply state

                let id = NoteId.ofOperationId (operation 2)
                let binding = Binding.Note(target state, id)

                let changes =
                    Remove.decide (context 3) environment (Some state) binding InputMap.empty
                    |> accepted

                let after = apply state changes
                Expect.isTrue after.Notes[id].Value.Removed "Accepted removal"

                Expect.equal
                    after.Notes[id].Value.Created
                    state.Notes[id].Value.Created
                    "Creation attribution is stable"

                Expect.equal
                    after.Notes[id].Revision
                    (Revision.create 2L |> Option.get)
                    "The note has its own chain"

                Remove.decide (context 4) environment (Some after) binding InputMap.empty
                |> expectRefusal (Refusal.Access AccessRefusal.ResourceUnavailable))
            testCase "missing notes and foreign parent selections remain unavailable" (fun () ->
                let state = family ()
                let missing = Binding.Note(target state, NoteId.ofOperationId (operation 7))

                Remove.decide (context 3) environment (Some state) missing InputMap.empty
                |> expectRefusal (Refusal.Access AccessRefusal.ResourceUnavailable)

                let foreign = CaseSelector.Id(CaseId.ofOperationId (operation 9))

                Remove.decide
                    (context 3)
                    environment
                    (Some state)
                    (Binding.Note(foreign, NoteId.ofOperationId (operation 7)))
                    InputMap.empty
                |> expectRefusal (Refusal.Access AccessRefusal.ResourceUnavailable))
        ]
