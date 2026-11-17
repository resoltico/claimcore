module ClaimCore.Tests.AggregateInvariantTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Tests.TaskBuilders

[<Tests>]
let tests =
    testList
        "aggregate invariants and derived indexes"
        [
            testCase
                "child changes advance the family sequence while recent ordering follows case changes"
                (fun () ->
                    let before = family ()

                    let changes =
                        ClaimCore.Domain.Tasks.Reminder.Create.decide
                            (context 2)
                            environment
                            (Some(DecisionFacts.fromReplay before))
                            (Binding.ReminderNew(target before))
                            (planInputs "Follow up" "2026-11-16")
                        |> accepted

                    let child =
                        { changes.Head with
                            Stamp =
                                { changes.Head.Stamp with
                                    RecordedAt = before.ChangedAt.AddSeconds 1.0
                                }
                        }

                    let state = apply before [ child ]

                    Expect.equal
                        state.Sequence
                        (Sequence.create 2L |> Option.get)
                        "Family concurrency observes children"

                    Expect.equal
                        (CaseFamily.index state |> Option.map _.ChangedAt)
                        (Some before.ChangedAt)
                        "The existing recent-order timestamp belongs to the case"

                    Expect.equal
                        (CaseFamily.index state
                         |> Option.bind _.FollowUp
                         |> Option.map CalendarDate.format)
                        (Some "2026-11-16")
                        "The child still updates the follow-up index"

                    let changes =
                        ClaimCore.Domain.Tasks.Case.Close.decide
                            (context 3)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (caseBinding state)
                            InputMap.empty
                        |> accepted

                    let caseChange =
                        { changes.Head with
                            Stamp =
                                { changes.Head.Stamp with
                                    RecordedAt = before.ChangedAt.AddSeconds 2.0
                                }
                        }

                    let after = apply state [ caseChange ]

                    Expect.equal
                        (CaseFamily.index after |> Option.map _.ChangedAt)
                        (Some caseChange.Stamp.RecordedAt)
                        "Case change attribution is authoritative")
            testCase
                "a valid standalone finance value cannot contradict accepted active entries"
                (fun () ->
                    let state = decided () |> pay 3 "2026-02-02" "25"

                    let changes =
                        ClaimCore.Domain.Tasks.Case.Close.decide
                            (context 4)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (caseBinding state)
                            InputMap.empty
                        |> accepted

                    let invalid =
                        match changes.Head.Value with
                        | PartValue.Case value ->
                            let content = value.Content |> Option.get

                            { changes.Head with
                                Value =
                                    PartValue.Case
                                        { value with
                                            Content =
                                                Some
                                                    { content with
                                                        Finance = FinancialPosition.undecided
                                                    }
                                        }
                            }
                        | _ -> failtest "Expected case value"

                    Expect.equal
                        (CaseFamily.apply (Some state) invalid)
                        (Error RecordViolation.InvalidPaymentState)
                        "The aggregate checks the actual owned entry set")
            testCase
                "note removal cannot retain current text or resurrect a removed identity"
                (fun () ->
                    let state = family ()
                    let textValue = NoteText.parse "Text" |> accepted

                    let changes =
                        ClaimCore.Domain.Tasks.Note.Add.decide
                            (context 2)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (Binding.NoteNew(target state))
                            (inputs [ text InputTarget.NoteText "Text" ])
                        |> accepted

                    let state = apply state changes
                    let id = NoteId.ofOperationId (operation 2)

                    let removed =
                        ClaimCore.Domain.Tasks.Note.Remove.decide
                            (context 3)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (Binding.Note(target state, id))
                            InputMap.empty
                        |> accepted
                        |> List.exactlyOne

                    let invalid =
                        { removed with
                            Value =
                                PartValue.Note
                                    {
                                        Removed = true
                                        Text = Some textValue
                                    }
                        }

                    Expect.equal
                        (CaseFamily.apply (Some state) invalid)
                        (Error RecordViolation.InvalidSubjectState)
                        "Removal clears the current value"

                    let state = apply state [ removed ]

                    let resurrect =
                        { removed with
                            Revision = Revision.create 3L |> Option.get
                            Stamp = (context 4).Stamp
                            Value =
                                PartValue.Note
                                    {
                                        Removed = false
                                        Text = Some textValue
                                    }
                        }

                    Expect.equal
                        (CaseFamily.apply (Some state) resurrect)
                        (Error RecordViolation.InvalidSubjectState)
                        "The existing identity remains a tombstone")
            testCase
                "active role assignments obey scope policy while absent assignments remain representable"
                (fun () ->
                    let state = person ()

                    let original =
                        ClaimCore.Domain.Tasks.People.Grant.decide
                            (context 3)
                            (peopleEnvironment newPrincipal)
                            (Some state)
                            (personBinding state)
                            (grantInputs Role.CaseReader "INSTALLATION")
                        |> accepted
                        |> List.exactlyOne

                    let invalidGrant =
                        {
                            ActorGrant.Role = Role.Owner
                            Scope = Scope.Case(CaseId.ofOperationId (operation 9))
                        }

                    let changed active =
                        match original.Value with
                        | PartValue.Person value ->
                            { original with
                                Value =
                                    PartValue.Person
                                        { value with
                                            Grants =
                                                [
                                                    {
                                                        Grant = invalidGrant
                                                        Active = active
                                                    }
                                                ]
                                        }
                            }
                        | _ -> failtest "Expected person value"

                    Expect.equal
                        (Person.apply (Some state) (changed true))
                        (Error RecordViolation.InvalidSubjectState)
                        "An active scope must be representable"

                    Expect.isOk
                        (Person.apply (Some state) (changed false))
                        "The accepted absent-assignment semantics are preserved")
            testCase
                "projection selection refuses foreign aggregates instead of dropping their changes"
                (fun () ->
                    let state = family ()

                    let foreign =
                        { initialChange with
                            Aggregate =
                                AggregateReference.Family(CaseId.ofOperationId (operation 9))
                        }

                    Expect.equal
                        (CaseFamily.rows state [ foreign ])
                        (Error RecordViolation.InvalidSubjectState)
                        "No silent projection omission")
            testCase "principal natural keys must commit the exact retained principal" (fun () ->
                let root =
                    ClaimCore.Domain.Tasks.People.Register.decide
                        (context 2)
                        (peopleEnvironment newPrincipal)
                        None
                        (Binding.PeopleNew Revision.zero)
                        (peopleInputs newPrincipal)
                    |> accepted
                    |> List.exactlyOne

                let different =
                    PrincipalKey.human "https://issuer.example/" "New-person" |> accepted

                Expect.equal
                    (Person.apply
                        None
                        { root with
                            NaturalKey = Some(PrincipalKey.naturalKey different)
                        })
                    (Error RecordViolation.InvalidSubjectState)
                    "Index identity cannot be substituted")
        ]
