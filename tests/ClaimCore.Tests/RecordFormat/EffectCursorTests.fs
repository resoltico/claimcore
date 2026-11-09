module ClaimCore.Tests.EffectCursorTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat

let private installation = Guid.Parse "00000000-0000-4000-8000-000000000001"

let private guid number =
    Guid.Parse(sprintf "00000000-0000-7000-8000-%012d" number)

let private position sequence index =
    EffectPosition.create (Sequence.create sequence |> Option.get) index
    |> Option.get

let private valid result =
    Result.defaultWith (fun error -> failtestf "Effect cursor: %A" error) result

[<Tests>]
let tests =
    testList
        "effect position cursors"
        [
            testCase
                "timeline continuation distinguishes two effects of one accepted operation"
                (fun () ->
                    let first, second = position 11L 0, position 11L 1
                    let a = Cursors.encodeTimeline installation "synthetic-scope" None first
                    let b = Cursors.encodeTimeline installation "synthetic-scope" None second

                    Expect.notEqual
                        a
                        b
                        "One operation sequence never collapses its effect positions"

                    Expect.equal
                        (Cursors.decodeTimeline installation "synthetic-scope" a |> valid)
                            .BeforePosition
                        first
                        "First effect round trips"

                    Expect.equal
                        (Cursors.decodeTimeline installation "synthetic-scope" b |> valid)
                            .BeforePosition
                        second
                        "Second effect round trips"

                    Expect.equal
                        (Cursors.decodeTimeline installation "another-scope" b)
                        (Error Refusal.InvalidCursor)
                        "Query/target scope remains bound"

                    Expect.isLessThan first second "Lexicographic order includes the ordinal")
            testCase
                "authority continuation binds installation target and effect ordinal"
                (fun () ->
                    let actor = ActorId.create (guid 2) |> Option.get
                    let other = ActorId.create (guid 3) |> Option.get
                    let expected = position 11L 5
                    let cursor = Cursors.encodeAuthorityHistory installation (Some actor) expected

                    Expect.equal
                        (Cursors.decodeAuthorityHistory installation (Some actor) cursor |> valid)
                        expected
                        "Compound authority effects preserve ordinal"

                    Expect.equal
                        (Cursors.decodeAuthorityHistory installation (Some other) cursor)
                        (Error Refusal.InvalidCursor)
                        "Another actor cannot consume the cursor"

                    Expect.equal
                        (Cursors.decodeAuthorityHistory installation None cursor)
                        (Error Refusal.InvalidCursor)
                        "An installation-wide query is a different selection"

                    Expect.equal
                        (Cursors.decodeAuthorityHistory (guid 4) (Some actor) cursor)
                        (Error Refusal.InvalidCursor)
                        "Foreign installation is rejected")
            testCase
                "notes and revision-conflict timeline positions retain their independent bindings"
                (fun () ->
                    let parent = CaseId.create (guid 2) |> Option.get
                    let other = CaseId.create (guid 3) |> Option.get
                    let expected = position 11L 0
                    let note = Cursors.encodeNotes installation parent expected

                    Expect.equal
                        (Cursors.decodeNotes installation parent note |> valid)
                        expected
                        "Notes expose composite provenance"

                    Expect.equal
                        (Cursors.decodeNotes installation other note)
                        (Error Refusal.InvalidCursor)
                        "Notes cannot cross parents"

                    let range =
                        {
                            ChangeRange.Entity = ChangeEntity.Case
                            ExpectedRevision = Revision.first
                            ActualRevision = Revision.create 3L |> Option.get
                        }

                    let conflict =
                        Cursors.encodeTimeline installation "conflict-scope" (Some range) expected

                    let actual =
                        Cursors.decodeTimeline installation "conflict-scope" conflict |> valid

                    Expect.equal
                        actual.Range
                        (Some range)
                        "Continuation preserves the immutable reviewed revision range"

                    Expect.equal
                        actual.BeforePosition
                        expected
                        "Conflict history uses the full effect position")
        ]
