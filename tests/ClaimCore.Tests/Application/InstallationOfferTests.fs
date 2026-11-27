module ClaimCore.Tests.InstallationOfferTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application.Reads

let private principal kind =
    PrincipalKey.ofKind kind "https://issuer.example/" "offer-control"
    |> Result.toOption
    |> Option.get

let private access principal roles cases =
    {
        AccessView.Actor =
            Some
                {
                    ActorId =
                        ActorId.create (Guid.Parse "10000000-0000-7000-8000-000000000001")
                        |> Option.get
                    Principal = principal
                    Enabled = true
                    Profile = None
                }
        Installation = roles
        Cases = Map.ofList cases
        HasScopedCases = not cases.IsEmpty
        HasAssignedRoles = not roles.IsEmpty || not cases.IsEmpty
    }

[<Tests>]
let tests =
    testCase
        "installation offers require current installation authority and only contain creators"
        (fun () ->
            let human = principal PrincipalKind.Human
            let service = principal PrincipalKind.Service

            let caseId =
                CaseId.create (Guid.Parse "10000000-0000-7000-8000-000000000002") |> Option.get

            Expect.isEmpty
                (Advice.installationOffers human (access human [] [ caseId, [ Role.Owner ] ]))
                "A scoped role does not manage the installation"

            Expect.isEmpty
                (Advice.installationOffers service (access service [ Role.Owner ] []))
                "Invalid service owner has no controls"

            Expect.equal
                (Advice.installationOffers human (access human [ Role.Owner ] []))
                [
                    InstallationOffer.PeopleAct(TaskId.ofLiteral "people.register")
                    InstallationOffer.PeopleAct(TaskId.ofLiteral "people.admit")
                ]
                "Existing-person actions require an actor selection")
