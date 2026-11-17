module ClaimCore.Tests.Tasks.PeopleRegisterTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Register = ClaimCore.Domain.Tasks.People.Register

[<Tests>]
let tests =
    testList
        "task people.register"
        [
            testCase
                "registration accepts exact principal identity and independent profile"
                (fun () ->
                    let state = person ()

                    Expect.equal
                        state.Id
                        (ActorId.ofOperationId (operation 2))
                        "The operation owns the actor identity"

                    Expect.equal state.Revision Revision.first "First person version"
                    Expect.equal state.Actor.Principal newPrincipal "Exact issuer and subject"

                    Expect.equal
                        (state.Actor.Profile
                         |> Option.map (fun profile -> PartyName.format profile.DisplayName))
                        (Some "New Person")
                        "Accepted profile"

                    Expect.isEmpty state.Grants "Registration grants no implicit rights"

                    Expect.equal
                        (PrincipalKey.naturalKey newPrincipal)
                        "813d7b7b3609c0a5c04001b2b7215fe92fef73bd2c8bea7856cbb8db480258e6"
                        "Independently calculated framed identity digest"

                    let differentCase =
                        PrincipalKey.human "https://issuer.example/" "New-person" |> accepted

                    Expect.notEqual
                        (PrincipalKey.naturalKey differentCase)
                        (PrincipalKey.naturalKey newPrincipal)
                        "Natural keys preserve subject case")
            testCase
                "duplicates, missing authority and independent malformed inputs refuse"
                (fun () ->
                    let state = person ()
                    let environment = peopleEnvironment newPrincipal

                    Register.decide
                        (context 3)
                        environment
                        (Some state)
                        (Binding.PeopleNew Revision.zero)
                        (peopleInputs newPrincipal)
                    |> expectRefusal (Refusal.People PeopleRefusal.ActorAlreadyRegistered)

                    Register.decide
                        (context 3)
                        { environment with
                            Requester = AccessView.empty
                        }
                        None
                        (Binding.PeopleNew Revision.zero)
                        (peopleInputs newPrincipal)
                    |> expectRefusal (Refusal.Access AccessRefusal.ResourceUnavailable)

                    let issues =
                        Register.check (
                            inputs
                                [
                                    text InputTarget.PrincipalKind "HUMAN"
                                    text InputTarget.PrincipalIssuer "http://insecure/"
                                    text InputTarget.PrincipalSubject ""
                                    text InputTarget.ProfileDisplayName ""
                                ]
                        )

                    Expect.equal
                        (issues |> List.map _.Input |> Set.ofList)
                        (Set.ofList
                            [
                                TaskInputId.Target InputTarget.PrincipalIssuer
                                TaskInputId.Target InputTarget.PrincipalSubject
                                TaskInputId.Target InputTarget.ProfileDisplayName
                            ])
                        "Independent controls accumulate")
        ]
