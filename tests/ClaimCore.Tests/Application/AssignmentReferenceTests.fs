module ClaimCore.Tests.AssignmentReferenceTests

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.TestSupport

let private id number =
    Guid.Parse(sprintf "e9711d82-1061-4f03-a519-%012d" number)

let private accepted =
    function
    | Ok value -> value
    | Error error -> failtestf "%A" error

[<Tests>]
let tests =
    testCase
        "assignment references preserve every requested relation and current eligibility"
        (fun () ->
            use store =
                new MemoryStore.Store(id 1, DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero))

            let parent = CaseFixtures.caseId (id 2)
            let other = CaseFixtures.caseId (id 3)

            let make number name enabled kind =
                {
                    Actor.ActorId = CaseFixtures.actorId (id number)
                    Principal = kind "https://assignment.example/" name |> accepted
                    Enabled = enabled
                    Profile =
                        Some(ActorProfile.parse { DisplayName = name; Contact = None } |> accepted)
                }

            let alice = make 4 "Alice" true PrincipalKey.human
            let bob = make 5 "Bob" false PrincipalKey.human
            let service = make 6 "Service" true PrincipalKey.service
            store.SeedActor alice [ Role.CaseHandler, Some parent ]
            store.SeedActor bob [ Role.CaseHandler, None ]
            store.SeedActor service [ Role.CaseHandler, Some parent ]

            let pairs =
                [
                    parent, alice.ActorId
                    parent, bob.ActorId
                    other, alice.ActorId
                    parent, service.ActorId
                ]

            let actual =
                (store :> IStore).Read
                    {
                        Work = fun tx -> tx.AssignedAccounts pairs
                        CancellationToken = CancellationToken.None
                    }
                |> fun task -> task.GetAwaiter().GetResult() |> accepted

            Expect.equal
                actual.Count
                4
                "Historical before/after actors on the same case cannot overwrite one another"

            let account parent (actor: Actor) = Map.find (parent, actor.ActorId) actual

            Expect.equal
                (account parent alice).DisplayName
                (DisplayName.Named(Inputs.name "Alice"))
                "Current saved names are retained"

            Expect.equal
                (account parent bob).DisplayName
                (DisplayName.Named(Inputs.name "Bob"))
                "Disabled actors keep their actual current name"

            Expect.equal
                (account parent alice).Availability
                ActorAvailability.Available
                "Case-local assignment roles apply to the requested relation"

            Expect.equal
                (account other alice).Availability
                ActorAvailability.NotEligible
                "Roles on one case do not make another relation eligible"

            Expect.equal
                (account parent bob).Availability
                ActorAvailability.Disabled
                "Disabled status is independent of role eligibility"

            Expect.equal
                (account parent service).Availability
                ActorAvailability.NotEligible
                "Corrupt service role rows never make a service assignable")
