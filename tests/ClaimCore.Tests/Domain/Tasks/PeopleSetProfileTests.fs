module ClaimCore.Tests.Tasks.PeopleSetProfileTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module SetProfile = ClaimCore.Domain.Tasks.People.SetProfile

[<Tests>]
let tests =
    testList
        "task people.setProfile"
        [
            testCase
                "profile replacement preserves identity and explicit null clears contact"
                (fun () ->
                    let state = person ()

                    let values =
                        inputs
                            [
                                text InputTarget.ProfileDisplayName "Changed Person"
                                TaskInputId.Target InputTarget.ProfileContact, InputValue.Null
                            ]

                    let after =
                        SetProfile.decide
                            (context 3)
                            (peopleEnvironment newPrincipal)
                            (Some state)
                            (personBinding state)
                            values
                        |> accepted
                        |> personApply state

                    Expect.equal after.Actor.Principal state.Actor.Principal "Identity is retained"

                    Expect.equal
                        (after.Actor.Profile
                         |> Option.map (fun profile ->
                             PartyName.format profile.DisplayName, profile.Contact))
                        (Some("Changed Person", None))
                        "Accepted replacement"

                    Expect.equal after.Grants state.Grants "Rights remain independent")
            testCase
                "current profile admission and reviewed revision refuse invalid replacement"
                (fun () ->
                    let state = person ()

                    Expect.isNonEmpty
                        (SetProfile.decide
                            (context 3)
                            (peopleEnvironment newPrincipal)
                            (Some state)
                            (personBinding state)
                            (inputs [ text InputTarget.ProfileDisplayName "" ])
                         |> expectInputs)
                        "Name is required"

                    SetProfile.decide
                        (context 3)
                        (peopleEnvironment newPrincipal)
                        (Some state)
                        (Binding.People(state.Id, Revision.zero))
                        (inputs profileInputs)
                    |> expectRefusal (Refusal.People PeopleRefusal.ActorChanged))
        ]
