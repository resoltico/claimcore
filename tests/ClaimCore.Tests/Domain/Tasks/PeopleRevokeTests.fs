module ClaimCore.Tests.Tasks.PeopleRevokeTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Revoke = ClaimCore.Domain.Tasks.People.Revoke

[<Tests>]
let tests =
    testList
        "task people.revoke"
        [
            testCase
                "revoking an absent grant is an accepted assignment without active-grant admission"
                (fun () ->
                    let state = person ()
                    let id = CaseId.ofOperationId (operation 9)

                    let environment =
                        { peopleEnvironment newPrincipal with
                            TargetCases = Map.ofList [ GrantTarget.CaseId id, id ]
                        }

                    let values =
                        inputs
                            [
                                text InputTarget.Role "OWNER"
                                TaskInputId.Scope, InputValue.Text "CASE"
                                TaskInputId.CaseId, InputValue.Text(CaseId.format id)
                            ]

                    let after =
                        Revoke.decide
                            (context 3)
                            environment
                            (Some state)
                            (personBinding state)
                            values
                        |> accepted
                        |> personApply state

                    Expect.isEmpty after.Grants "No right is manufactured"

                    Expect.equal
                        (Revision.value after.Revision)
                        2L
                        "The accepted no-op has a reviewed revision")
            testCase "the last enabled human owner cannot lose owner rights" (fun () ->
                let state = owner ()

                let environment =
                    { peopleEnvironment newPrincipal with
                        EnabledHumanOwners = 1
                    }

                Revoke.decide
                    (context 3)
                    environment
                    (Some state)
                    (personBinding state)
                    (grantInputs Role.Owner "INSTALLATION")
                |> expectRefusal (Refusal.People PeopleRefusal.LastOwner)

                let after =
                    Revoke.decide
                        (context 3)
                        { environment with
                            EnabledHumanOwners = 2
                        }
                        (Some state)
                        (personBinding state)
                        (grantInputs Role.Owner "INSTALLATION")
                    |> accepted
                    |> personApply state

                Expect.isEmpty after.Grants "Another enabled human owner permits revocation")
        ]
