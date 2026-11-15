module ClaimCore.Tests.Tasks.PeopleEraseProfileTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module EraseProfile = ClaimCore.Domain.Tasks.People.EraseProfile

[<Tests>]
let tests =
    testList
        "task people.eraseProfile"
        [
            testCase
                "profile erasure names its owner and preserves identity enabled state and rights"
                (fun () ->
                    let state = owner ()
                    let environment = peopleEnvironment newPrincipal

                    let changes =
                        EraseProfile.decide
                            (context 3)
                            environment
                            (Some state)
                            (personBinding state)
                            InputMap.empty
                        |> accepted

                    Expect.equal
                        changes.Head.Erases
                        (Some(CompartmentOwner.Profile state.Id))
                        "Explicit clearing authority"

                    let after = personApply state changes
                    Expect.isNone after.Actor.Profile "Private profile unavailable"
                    Expect.equal after.Actor.Principal state.Actor.Principal "Retained identity"
                    Expect.equal after.Actor.Enabled state.Actor.Enabled "Enabled state retained"
                    Expect.equal after.Grants state.Grants "Grant accepting positions retained"

                    Expect.equal
                        after.ProfileErasure
                        (Some((context 3).Stamp.Position))
                        "Erasure provenance"

                    let restored =
                        ClaimCore.Domain.Tasks.People.SetProfile.decide
                            (context 4)
                            environment
                            (Some after)
                            (personBinding after)
                            (inputs profileInputs)
                        |> accepted
                        |> personApply after

                    Expect.isSome restored.Actor.Profile "A later explicit replacement is allowed"
                    Expect.isNone restored.ProfileErasure "The new current profile is available")
        ]
