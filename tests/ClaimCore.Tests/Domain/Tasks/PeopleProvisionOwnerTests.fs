module ClaimCore.Tests.Tasks.PeopleProvisionOwnerTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Provision = ClaimCore.Domain.Tasks.People.ProvisionOwner

[<Tests>]
let tests =
    testList
        "task people.provisionOwner"
        [
            testCase "the operator may initialize an empty installation with owner alone" (fun () ->
                let state = owner ()
                Expect.equal state.Grants.Count 1 "Additional initial roles are optional"

                Expect.isTrue
                    (Map.containsKey
                        {
                            ActorGrant.Role = Role.Owner
                            Scope = Scope.Installation
                        }
                        state.Grants)
                    "Owner is implicit"

                Expect.equal
                    Provision.declaration.Exposure
                    TaskExposure.Operator
                    "No client exposure"

                Expect.equal
                    Provision.declaration.Collections.Head.MinimumRows
                    0
                    "The declaration preserves operator acquisition")
            testCase
                "nonempty installations and service principals cannot be initialized"
                (fun () ->
                    let environment = peopleEnvironment newPrincipal

                    Provision.decide
                        (context 2)
                        environment
                        None
                        (Binding.PeopleNew Revision.zero)
                        (bootstrapInputs newPrincipal)
                    |> expectRefusal (Refusal.People PeopleRefusal.ActorAlreadyRegistered)

                    let service =
                        PrincipalKey.service "https://issuer.example/" "service" |> accepted

                    Provision.decide
                        (context 2)
                        { peopleEnvironment service with
                            ActorCount = 0
                            Requester = AccessView.empty
                        }
                        None
                        (Binding.PeopleNew Revision.zero)
                        (bootstrapInputs service)
                    |> expectRefusal (Refusal.People PeopleRefusal.HumanPrincipalRequired))
        ]
