module ClaimCore.Tests.Tasks.PeopleDisableTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Disable = ClaimCore.Domain.Tasks.People.Disable

[<Tests>]
let tests =
    testList
        "task people.disable"
        [
            testCase
                "disabling preserves rights and the last-owner protection uses current facts"
                (fun () ->
                    let state = owner ()

                    let environment =
                        { peopleEnvironment newPrincipal with
                            EnabledHumanOwners = 1
                        }

                    Expect.equal
                        (Disable.availability environment (Some state) (personBinding state))
                            .Blocked
                        (Some(Refusal.People PeopleRefusal.LastOwner))
                        "Availability is task-owned"

                    Disable.decide
                        (context 3)
                        environment
                        (Some state)
                        (personBinding state)
                        InputMap.empty
                    |> expectRefusal (Refusal.People PeopleRefusal.LastOwner)

                    let after =
                        Disable.decide
                            (context 3)
                            { environment with
                                EnabledHumanOwners = 2
                            }
                            (Some state)
                            (personBinding state)
                            InputMap.empty
                        |> accepted
                        |> personApply state

                    Expect.isFalse after.Actor.Enabled "Disabled"
                    Expect.equal after.Grants state.Grants "Disable does not revoke"

                    Expect.equal
                        after.Actor.Profile
                        state.Actor.Profile
                        "Profile remains private accepted data")
        ]
