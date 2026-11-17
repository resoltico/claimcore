module ClaimCore.Tests.Tasks.PeopleAdmitTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Admit = ClaimCore.Domain.Tasks.People.Admit

let private values rows =
    InputMap.create
        (Map.ofList (principalInputs newPrincipal @ profileInputs))
        (Map.ofList [ TaskCollectionId.PeopleGrants, rows ])
    |> Option.get

let private row role =
    (grantInputs role "INSTALLATION").Values

[<Tests>]
let tests =
    testList
        "task people.admit"
        [
            testCase
                "profile and grants are accepted together with their accepting position"
                (fun () ->
                    let changes =
                        Admit.decide
                            (context 2)
                            (peopleEnvironment newPrincipal)
                            None
                            (Binding.PeopleNew Revision.zero)
                            (values [ row Role.CaseReader; row Role.PaymentRecorder ])
                        |> accepted

                    Expect.equal changes.Length 1 "One person part"
                    let state = Person.apply None changes.Head |> accepted
                    Expect.equal state.Grants.Count 2 "Accepted grants"

                    Expect.equal
                        (Map.find
                            {
                                ActorGrant.Role = Role.CaseReader
                                Scope = Scope.Installation
                            }
                            state.Grants)
                        (context 2).Stamp.Position
                        "Stable accepting position")
            testCase
                "empty and duplicate grants refuse while row syntax issues retain ordinals"
                (fun () ->
                    Expect.isNonEmpty
                        (Admit.decide
                            (context 2)
                            (peopleEnvironment newPrincipal)
                            None
                            (Binding.PeopleNew Revision.zero)
                            (values [])
                         |> expectInputs)
                        "At least one grant"

                    let duplicate =
                        Admit.decide
                            (context 2)
                            (peopleEnvironment newPrincipal)
                            None
                            (Binding.PeopleNew Revision.zero)
                            (values [ row Role.CaseReader; row Role.CaseReader ])
                        |> expectInputs

                    Expect.equal
                        duplicate.Head.Violation
                        Violation.DuplicateGrant
                        "No duplicate accepted rights"

                    let issues =
                        Admit.check (
                            values
                                [
                                    row Role.CaseReader
                                    Map.ofList
                                        [
                                            text InputTarget.Role "CASE_READER"
                                            TaskInputId.Scope, InputValue.Text "CASE"
                                        ]
                                ]
                        )

                    Expect.equal
                        (issues
                         |> List.map (fun issue -> issue.Row |> Option.map TaskRowTarget.ordinal))
                        [ Some 1 ]
                        "The submitted second row owns the issue")
        ]
