module ClaimCore.Tests.Tasks.PeopleEnableTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Enable = ClaimCore.Domain.Tasks.People.Enable

[<Tests>]
let tests =
    testList
        "task people.enable"
        [
            testCase "enabling and re-enabling retain accepted rights and profile" (fun () ->
                let state = person ()

                let after =
                    Enable.decide
                        (context 3)
                        (peopleEnvironment newPrincipal)
                        (Some state)
                        (personBinding state)
                        InputMap.empty
                    |> accepted
                    |> personApply state

                Expect.isTrue after.Actor.Enabled "Enabled"
                Expect.equal after.Actor.Profile state.Actor.Profile "Profile preserved"
                Expect.equal after.Grants state.Grants "Rights preserved"

                Expect.equal
                    (Revision.value after.Revision)
                    2L
                    "A reviewed re-enable remains an accepted operation")
            testCase "a missing or substituted actor is unavailable" (fun () ->
                let state = person ()

                Enable.decide
                    (context 3)
                    (peopleEnvironment newPrincipal)
                    None
                    (personBinding state)
                    InputMap.empty
                |> expectRefusal (Refusal.People PeopleRefusal.ActorUnknown)

                Enable.decide
                    (context 3)
                    (peopleEnvironment principal)
                    (Some state)
                    (personBinding state)
                    InputMap.empty
                |> expectRefusal (Refusal.Access AccessRefusal.ResourceUnavailable))
        ]
