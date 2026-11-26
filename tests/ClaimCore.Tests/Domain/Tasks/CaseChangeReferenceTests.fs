module ClaimCore.Tests.Tasks.CaseChangeReferenceTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Tests.TaskBuilders

module Change = ClaimCore.Domain.Tasks.Case.ChangeReference

let private run number state owner raw =
    Change.decide
        (context number)
        { environment with
            ReferenceOwner = owner
        }
        (Some(DecisionFacts.fromReplay state))
        (caseBinding state)
        (inputs [ text InputTarget.NewCaseReference raw ])

[<Tests>]
let tests =
    testList
        "case reference changes"
        [
            testCase
                "new and returning references carry exactly their required ownership effects"
                (fun () ->
                    let before = family ()
                    let identity = before.Id
                    let first = run 2 before (fun _ -> None) "REF-2" |> accepted
                    Expect.equal first.Head.NaturalKey (Some "REF-2") "First use introduces a key"

                    let second = apply before first

                    Expect.equal
                        (CaseReference.format second.Reference)
                        "REF-2"
                        "The case now uses that spelling"

                    let returned = run 3 second (fun _ -> Some identity) "TASK-1" |> accepted

                    Expect.isNone
                        returned.Head.NaturalKey
                        "A returning reference introduces no key"

                    let original = apply second returned
                    let respelled = run 4 original (fun _ -> Some identity) "task-1" |> accepted
                    Expect.isNone respelled.Head.NaturalKey "A respelling introduces no key"

                    Expect.equal
                        (CaseReference.format (apply original respelled).Reference)
                        "task-1"
                        "The new spelling is stored")
            testCase
                "reject an unchanged spelling, a foreign key, and an invalid reference"
                (fun () ->
                    let state = family ()

                    run 2 state (fun _ -> Some state.Id) "TASK-1"
                    |> expectRefusal (Refusal.Case CaseRefusal.CorrectionNoChanges)

                    let other = CaseId.ofOperationId (operation 80)

                    run 2 state (fun _ -> Some other) "REF-2"
                    |> expectRefusal (Refusal.Case CaseRefusal.ReferenceUnavailable)

                    let issues = run 2 state (fun _ -> None) "invalid reference" |> expectInputs

                    Expect.equal
                        (issues |> List.map _.Violation)
                        [ Violation.ReferenceFormat ]
                        "The existing reference grammar applies")
            testCase "generated references expose no correction offer or ownership check" (fun () ->
                let candidate = CaseReference.parse "CC-0000-0000-0000-0000-98" |> accepted

                let state =
                    match initialChange.Value with
                    | PartValue.Case value ->
                        let initial =
                            { initialChange with
                                NaturalKey = Some "CC-0000-0000-0000-0000-98"
                                Value =
                                    PartValue.Case
                                        { value with
                                            Reference = Some candidate
                                        }
                            }

                        CaseFamily.apply None initial |> accepted
                    | _ -> failtest "Expected case opening effect."

                let environment =
                    { environment with
                        ReferenceOwner =
                            fun _ ->
                                failtest "An inapplicable correction must not look up ownership."
                    }

                let facts = Some(DecisionFacts.fromReplay state)
                let binding = caseBinding state
                let available = Change.availability environment facts binding
                Expect.isFalse available.Applicable "Generated case correction is not offered"

                let result =
                    Change.decide
                        (context 2)
                        environment
                        facts
                        binding
                        (inputs [ text InputTarget.NewCaseReference "REF-2" ])

                match result with
                | Error(Rejection.Refused(Refusal.Case(CaseRefusal.NotOffered _) as refusal)) ->
                    Expect.equal
                        available.Blocked
                        (Some refusal)
                        "The offer and write use the same gate"
                | actual -> failtestf "Expected an inapplicable correction, got %A" actual)
            testCase "opt-in custom correction cannot claim the generated namespace" (fun () ->
                for reference in [ "cc-"; "cC-BAD"; "CC-0000-0000-0000-0000-98" ] do
                    let issue =
                        run
                            2
                            (family ())
                            (fun _ -> failtest "Rejected syntax queried ownership.")
                            reference
                        |> expectInputs
                        |> List.exactlyOne

                    Expect.equal
                        issue.Violation
                        Violation.ReferenceFormat
                        "Generated namespace is reserved")
        ]
