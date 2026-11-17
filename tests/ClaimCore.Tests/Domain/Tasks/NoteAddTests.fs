module ClaimCore.Tests.Tasks.NoteAddTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Add = ClaimCore.Domain.Tasks.Note.Add

[<Tests>]
let tests =
    testList
        "task note.add"
        [
            testCase
                "closed active parents accept exact text without consuming a case revision"
                (fun () ->
                    let state = family () |> setCase Lifecycle.Active CaseStatus.Closed

                    let changes =
                        Add.decide
                            (context 3)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (Binding.NoteNew(target state))
                            (inputs [ text InputTarget.NoteText "Exact\nprivate text" ])
                        |> accepted

                    let after = apply state changes
                    Expect.equal after.Case.Revision state.Case.Revision "Only the note changes"
                    Expect.equal changes.Length 1 "One part"

                    match changes.Head.Value with
                    | PartValue.Note value ->
                        Expect.equal
                            (value.Text |> Option.map NoteText.format)
                            (Some "Exact\nprivate text")
                            "Accepted text travels in the value"
                    | _ -> failtest "Expected a note value"

                    Expect.equal after.Notes.Count 1 "Aggregate tracks note identity")
            testCase "voided and erased parents refuse at the selected state" (fun () ->
                for lifecycle, headline in
                    [
                        Lifecycle.Voided, CaseHeadline.Voided
                        Lifecycle.Erased, CaseHeadline.Erased
                    ] do
                    let state = family () |> setCase lifecycle CaseStatus.Opened

                    Add.decide
                        (context 3)
                        environment
                        (Some(DecisionFacts.fromReplay state))
                        (Binding.NoteNew(target state))
                        (inputs [ text InputTarget.NoteText "note" ])
                    |> expectRefusal (Refusal.Case(CaseRefusal.NotOffered headline)))
            testCase "text admission and identity reuse are enforced" (fun () ->
                let state = family ()
                let binding = Binding.NoteNew(target state)

                let invalid =
                    Add.decide
                        (context 2)
                        environment
                        (Some(DecisionFacts.fromReplay state))
                        binding
                        (inputs [ text InputTarget.NoteText "\r" ])

                Expect.isNonEmpty (expectInputs invalid) "Current text rules apply"
                let values = inputs [ text InputTarget.NoteText "note" ]

                let state =
                    Add.decide
                        (context 2)
                        environment
                        (Some(DecisionFacts.fromReplay state))
                        binding
                        values
                    |> accepted
                    |> apply state

                Add.decide
                    (context 2)
                    environment
                    (Some(DecisionFacts.fromReplay state))
                    binding
                    values
                |> expectRefusal Refusal.IdempotencyConflict)
        ]
